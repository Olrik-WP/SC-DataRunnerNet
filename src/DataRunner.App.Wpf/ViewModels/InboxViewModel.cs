using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DataRunner.App.Services;
using DataRunner.Core.Abstractions;
using DataRunner.Core.Models;
using Microsoft.Win32;

namespace DataRunner.App.ViewModels;

/// <summary>
/// Lists screenshots queued for review and lets the user open one in the editor.
/// In V1 the inbox is populated by:
///  - the user manually opening files via ImportCommand
///  - a future ScreenshotFolderWatcher that drops files in a watched folder
/// </summary>
public sealed partial class InboxViewModel : ObservableObject
{
    public ObservableCollection<InboxItem> Items { get; } = new();

    /// <summary>
    /// Items currently multi-selected in the ListBox. The view binds this two-way
    /// via a behavior so we can drive RemoveSelected on the full multi-selection
    /// without relying on ListBox.SelectedItems (which is read-only on the
    /// binding side).
    /// </summary>
    public ObservableCollection<InboxItem> SelectedItems { get; } = new();

    [ObservableProperty] private InboxItem? _selectedItem;
    [ObservableProperty] private ScreenshotEditViewModel? _currentEditor;

    /// <summary>
    /// True when the currently selected item is still being processed by the OCR
    /// pipeline. The view binds the editor placeholder against this flag to show
    /// "OCR in progress" instead of the generic "pick a screenshot" hint, and
    /// to make sure no half-baked editor is rendered before the submission is
    /// fully populated.
    /// </summary>
    [ObservableProperty] private bool _isSelectedProcessing;

    /// <summary>
    /// Holds the currently observed item so we can unhook the property-changed
    /// listener when the user picks another one. Without this we'd leak handlers
    /// every time the selection changes.
    /// </summary>
    private InboxItem? _selectionListener;

    /// <summary>
    /// Set by App startup once the DI container is ready.
    /// We can't inject it via the constructor because <see cref="Services.OcrCoordinator"/>
    /// itself depends on this view model -> circular dependency.
    /// </summary>
    public Action<string>? OnImportRequested { get; set; }

    /// <summary>
    /// Set by App startup once the watcher singleton is resolved. Forces a
    /// folder rescan from the UI for the given <see cref="TimeSpan"/> window
    /// (pass <see cref="Services.ScreenshotFolderWatcher.WindowAll"/> for "all
    /// files in folder, no cutoff"). Returns a structured result so the VM can
    /// surface clear feedback (no folder configured, missing folder, X files
    /// picked up, etc.).
    /// </summary>
    public Func<TimeSpan, Task<Services.ScreenshotFolderWatcher.RescanResult>>? OnRescanRequested { get; set; }

    /// <summary>
    /// Resolved at startup so the "Open screenshots folder" button can read
    /// the user's configured path without taking a DI dependency on
    /// <see cref="SettingsViewModel"/> (which would create a cycle).
    /// </summary>
    public Func<string?>? GetScreenshotsFolderPath { get; set; }

    [RelayCommand]
    private void OpenScreenshotsFolder()
    {
        var folder = GetScreenshotsFolderPath?.Invoke();
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return;

        Process.Start(new ProcessStartInfo
        {
            FileName = folder,
            UseShellExecute = true,
        });
    }

    /// <summary>
    /// True when the inbox column is collapsed to a thin strip with just a
    /// re-expand button. The view binds the column width and the inner
    /// content visibility to this flag.
    /// </summary>
    [ObservableProperty] private bool _isCollapsed;

    private IAppPreferences? _prefs;

    /// <summary>Wired by App.OnStartup once DI is ready, so the toggle
    /// persists across restarts.</summary>
    public void AttachPreferences(IAppPreferences prefs)
    {
        _prefs = prefs;
        IsCollapsed = prefs.InboxCollapsed;
    }

    partial void OnIsCollapsedChanged(bool value)
    {
        if (_prefs is null) return;
        _prefs.InboxCollapsed = value;
        _ = _prefs.SaveAsync();
    }

    [RelayCommand]
    private void ToggleCollapsed() => IsCollapsed = !IsCollapsed;

    /// <summary>
    /// Collapsed inbox rail: pick one queued screenshot without expanding the list.
    /// Replaces any multi-selection with this single item so the editor + preview match.
    /// </summary>
    [RelayCommand]
    private void SelectFromCollapsedRail(InboxItem? item)
    {
        if (item is null) return;
        SelectedItems.Clear();
        SelectedItems.Add(item);
        SelectedItem = item;
    }

    public InboxViewModel()
    {
        Items.CollectionChanged += OnItemsCollectionChanged;
        SelectedItems.CollectionChanged += OnSelectedItemsChanged;
    }

    private void OnItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Renumber first so the queue badges stay accurate, then refresh the
        // batch counters since adding / removing an item changes how many are
        // ready, validated, or failed.
        RenumberQueuePositions();
        if (e.OldItems is not null)
            foreach (InboxItem it in e.OldItems) it.PropertyChanged -= OnItemStatusChanged;
        if (e.NewItems is not null)
            foreach (InboxItem it in e.NewItems) it.PropertyChanged += OnItemStatusChanged;
        RecomputeBatchCounters();
    }

    private void OnItemStatusChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(InboxItem.Status)) return;
        RecomputeBatchCounters();
    }

    /// <summary>
    /// Refreshes each item's <see cref="InboxItem.QueuePosition"/> (1-based row
    /// label in the inbox list). Shown as a small badge so screenshots are easy
    /// to tell apart at a glance.
    /// </summary>
    private void RenumberQueuePositions()
    {
        for (var i = 0; i < Items.Count; i++)
            Items[i].QueuePosition = i + 1;
    }

    private void OnSelectedItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Multi-selection drives the visibility of the bulk delete dropdown.
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectionCount));
        RemoveSelectedCommand.NotifyCanExecuteChanged();
    }

    /// <summary>True when at least one item is selected (1 or N). Used to show
    /// the bulk delete button label / count in the toolbar.</summary>
    public bool HasSelection => SelectedItems.Count > 0;

    /// <summary>Convenience binding for the trash button label
    /// ("Remove (3)" / "Remove" depending on selection size).</summary>
    public int SelectionCount => SelectedItems.Count;

    partial void OnSelectedItemChanged(InboxItem? value)
    {
        // FIRST: persist whatever the user has typed in the OUTGOING editor
        // back onto its bound item. Without this, every selection change
        // would silently drop pending edits — Tab=Sell pick, manual price
        // corrections, terminal disambiguation, the lot. SaveDraftToBoundItem
        // is a no-op when the editor isn't bound to an item, so it's safe
        // even on the very first selection.
        CurrentEditor?.SaveDraftToBoundItem();

        // Detach from any previously observed item so we don't double-handle
        // status transitions after the selection moves.
        if (_selectionListener is not null)
        {
            _selectionListener.PropertyChanged -= OnSelectedItemPropertyChanged;
            _selectionListener = null;
        }

        if (value is null)
        {
            CurrentEditor = null;
            IsSelectedProcessing = false;
            return;
        }

        // Keep observing this item's Status so the editor auto-opens once OCR
        // completes (Pending/Processing -> Ready/Review). Without this, the
        // user would have to click the item again after OCR finishes.
        _selectionListener = value;
        value.PropertyChanged += OnSelectedItemPropertyChanged;
        OpenEditorIfReady(value);
    }

    private void OnSelectedItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(InboxItem.Status)) return;
        if (sender is not InboxItem item) return;
        if (!ReferenceEquals(item, SelectedItem)) return;
        OpenEditorIfReady(item);
    }

    /// <summary>
    /// Loads the editor for <paramref name="item"/> only if its OCR is done.
    /// While the item is Pending or Processing we leave the editor closed and
    /// surface a dedicated placeholder via <see cref="IsSelectedProcessing"/>.
    /// Otherwise (Ready / Review / Sent / Failed) we instantiate a fresh editor
    /// and hand the item to it.
    /// </summary>
    private void OpenEditorIfReady(InboxItem item)
    {
        var processing = item.Status is InboxStatus.Pending or InboxStatus.Processing;
        IsSelectedProcessing = processing;

        if (processing)
        {
            CurrentEditor = null;
            return;
        }

        var editor = App.Resolve<ScreenshotEditViewModel>();
        // Wire the auto-advance callback BEFORE Load so a Validate click on a
        // pre-validated item that fires synchronously (defensive — should not
        // happen) still finds the right inbox VM hook to navigate from.
        editor.OnValidatedRequestNext = SelectNextNonValidatedAfter;
        editor.Load(item);
        CurrentEditor = editor;
    }

    [RelayCommand]
    private void Import()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Pick a Star Citizen commodity terminal screenshot",
            Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp",
            Multiselect = true,
        };
        if (dlg.ShowDialog() != true) return;

        var handler = OnImportRequested;
        foreach (var path in dlg.FileNames)
        {
            if (handler is not null)
            {
                handler(path);
            }
            else
            {
                // Coordinator not yet wired (very early startup); just queue the file.
                Items.Add(new InboxItem
                {
                    ImagePath = path,
                    DisplayName = InboxItem.FormatDisplayName(path),
                    Status = InboxStatus.Pending,
                    AddedAt = DateTimeOffset.Now,
                    SourcePaths = new() { path },
                });
            }
        }
    }

    /// <summary>
    /// Rescans the watched folder. Parameter is the window in minutes; pass
    /// <c>0</c> (or <c>"0"</c>) to import EVERY screenshot in the folder.
    ///
    /// Bound from the InboxView dropdown:
    ///   "Last 5 minutes"      → 5
    ///   "Last hour"           → 60
    ///   "Last 24 hours"       → 1440
    ///   "All in folder"       → 0
    /// </summary>
    [RelayCommand]
    private async Task RescanFolderAsync(object? parameter)
    {
        if (OnRescanRequested is null) return;

        // Parameter may come in as int (from code) or string (from XAML
        // CommandParameter). Default to 5 min if absent.
        var minutes = parameter switch
        {
            int i => i,
            string s when int.TryParse(s, out var n) => n,
            _ => 5,
        };
        var window = minutes <= 0
            ? Services.ScreenshotFolderWatcher.WindowAll
            : TimeSpan.FromMinutes(minutes);

        var result = await OnRescanRequested(window).ConfigureAwait(true);
        var dialog = App.Resolve<Services.IDialogService>();

        if (!result.RanScan)
        {
            // Folder not configured / not found — actionable error so the user
            // understands what to fix.
            dialog.ShowError("Cannot rescan folder", result.Reason ?? "Unknown reason.");
            return;
        }

        if (result.FilesPicked == 0)
        {
            dialog.ShowInfo("Nothing new",
                $"No screenshots were found in:\n{result.FolderUsed}\n\n{result.Reason}");
            return;
        }

        // Files were enqueued — no dialog needed; they'll appear in the inbox
        // with their own status. Just nothing to do here.
    }

    /// <summary>
    /// Removes EVERY currently multi-selected item. Falls back to the single
    /// <see cref="SelectedItem"/> when the multi-selection collection is
    /// empty (single-click case).
    ///
    /// Bug fix vs. the previous implementation: we used to drop only
    /// <see cref="SelectedItem"/>, which meant marquee-selecting 5 items and
    /// hitting Delete only removed 1. Now we iterate on <see cref="SelectedItems"/>.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRemoveSelected))]
    private void RemoveSelected()
    {
        // Snapshot the multi-selection list to avoid mutating it while we
        // iterate — Items.Remove cascades into SelectedItems removal.
        var targets = SelectedItems.Count > 0
            ? SelectedItems.ToList()
            : (SelectedItem is null ? new List<InboxItem>() : new List<InboxItem> { SelectedItem });
        if (targets.Count == 0) return;

        SelectedItem = null;
        CurrentEditor = null;
        SelectedItems.Clear();

        foreach (var t in targets)
            Items.Remove(t);
    }

    private bool CanRemoveSelected() => SelectedItems.Count > 0 || SelectedItem is not null;

    /// <summary>
    /// Bulk-remove every item whose status is <see cref="InboxStatus.Sent"/>.
    /// Used by the trash button's dropdown menu after a batch send to clean
    /// up the inbox in one click instead of clicking each card. No confirm
    /// dialog — Sent items are terminal and the screenshot files were already
    /// deleted (or kept on disk on test sends) by the submission flow itself.
    /// </summary>
    [RelayCommand]
    private void RemoveAllSent() => RemoveItemsWhere(i => i.Status == InboxStatus.Sent);

    /// <summary>Bulk-remove every <see cref="InboxStatus.Failed"/> item. Same
    /// rationale as <see cref="RemoveAllSent"/>.</summary>
    [RelayCommand]
    private void RemoveAllFailed() => RemoveItemsWhere(i => i.Status == InboxStatus.Failed);

    /// <summary>
    /// Drops EVERY item from the inbox. Confirmed via <see cref="IDialogService"/>
    /// so the user doesn't lose unsent work to a stray click. Items with status
    /// <see cref="InboxStatus.Pending"/> / <see cref="InboxStatus.Processing"/>
    /// are kept (we don't want to abort an OCR run mid-flight by yanking the
    /// item out from under it).
    /// </summary>
    [RelayCommand]
    private void RemoveAll()
    {
        var dialog = App.Resolve<IDialogService>();
        var deletable = Items.Where(IsDeletableInBulk).ToList();
        if (deletable.Count == 0)
        {
            dialog.ShowInfo("Nothing to remove",
                "No removable items in the inbox (items currently being processed by OCR are kept).");
            return;
        }

        var confirm = System.Windows.MessageBox.Show(
            $"Remove {deletable.Count} item(s) from the inbox?\n\n" +
            "Items currently being processed by OCR are kept; sent items keep their History entry.",
            "Remove all",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Question,
            System.Windows.MessageBoxResult.No);
        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        RemoveItemsWhere(IsDeletableInBulk);
    }

    private static bool IsDeletableInBulk(InboxItem item)
        => item.Status is not (InboxStatus.Pending or InboxStatus.Processing or InboxStatus.Sending);

    private void RemoveItemsWhere(Func<InboxItem, bool> predicate)
    {
        var snapshot = Items.Where(predicate).ToList();
        if (snapshot.Count == 0) return;

        // Drop the editor binding if its bound item is being removed so we
        // don't leave a stale view-model referencing a deleted card.
        if (SelectedItem is not null && predicate(SelectedItem))
        {
            SelectedItem = null;
            CurrentEditor = null;
        }

        SelectedItems.Clear();
        foreach (var t in snapshot)
            Items.Remove(t);
    }

    // ---- Batch send state ----
    // The actual smart-split + sequential POST loop lives in IBatchPlanner +
    // IBatchSubmitter (Services/). The inbox view-model only owns the wiring
    // between the user clicks and those services, plus the inbox-side state
    // (counters, in-flight flag, cancellation source).

    /// <summary>
    /// Set by App startup to wire the Send batch button to <see cref="IBatchSubmitter"/>.
    /// We use a callback rather than constructor injection so this view-model
    /// can stay a singleton without pulling the entire submission stack into
    /// the early app startup graph (the planner / submitter resolve ICatalogProvider,
    /// IUexApiClient and friends — heavy).
    /// </summary>
    private Func<IReadOnlyList<InboxItem>, CancellationToken, Task>? _onSendBatchRequested;
    public Func<IReadOnlyList<InboxItem>, CancellationToken, Task>? OnSendBatchRequested
    {
        get => _onSendBatchRequested;
        set
        {
            _onSendBatchRequested = value;
            // Wiring landed late — re-evaluate the batch commands so the
            // toolbar buttons enable themselves on next user interaction
            // instead of waiting for the next status change to nudge them.
            SendBatchCommand.NotifyCanExecuteChanged();
            RetryFailedCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>
    /// Cancellation source for the current batch run. Owned solely by the
    /// inbox view-model; <see cref="StopBatch"/> cancels it and
    /// <see cref="EndBatch"/> resets it. Null when no batch is running.
    /// </summary>
    private CancellationTokenSource? _batchCts;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBatchIdle))]
    [NotifyCanExecuteChangedFor(nameof(SendBatchCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopBatchCommand))]
    private bool _isBatchInProgress;

    public bool IsBatchIdle => !IsBatchInProgress;

    /// <summary>Number of items currently flagged Validated and ready for the
    /// next batch send. Bound by the toolbar as the "X" of "X / Y ready".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BatchSummary))]
    [NotifyCanExecuteChangedFor(nameof(SendBatchCommand))]
    private int _validatedCount;

    /// <summary>Number of items still expected to land in the next batch
    /// (everything that is not Sent / Failed and not currently OCR-processing).
    /// Forms the denominator of the toolbar "X / Y ready" label.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BatchSummary))]
    [NotifyCanExecuteChangedFor(nameof(SendBatchCommand))]
    private int _pendingCount;

    /// <summary>Number of items in the Failed state — drives the visibility of
    /// the Retry failed button.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFailedItems))]
    [NotifyCanExecuteChangedFor(nameof(RetryFailedCommand))]
    private int _failedCount;

    public bool HasFailedItems => FailedCount > 0;

    /// <summary>Human-readable "X / Y ready" label for the Send batch button.</summary>
    public string BatchSummary => $"{ValidatedCount} / {ValidatedCount + PendingCount} ready";

    private void RecomputeBatchCounters()
    {
        var validated = 0;
        var pending = 0;
        var failed = 0;
        foreach (var i in Items)
        {
            switch (i.Status)
            {
                case InboxStatus.Validated: validated++; break;
                case InboxStatus.Failed: failed++; break;
                case InboxStatus.Sent: break;
                // Anything else (Pending / Processing / Ready / Review / Sending)
                // counts toward "still expected". We deliberately DO include
                // Sending here so the denominator stays stable while a batch
                // is mid-flight (counters tick down only as items land).
                default: pending++; break;
            }
        }
        ValidatedCount = validated;
        PendingCount = pending;
        FailedCount = failed;
    }

    /// <summary>
    /// Send batch is enabled iff:
    ///   - the wiring is present (App.OnStartup ran);
    ///   - no batch is already in flight;
    ///   - at least one item is Validated;
    ///   - every NON-terminal item is Validated (i.e. nothing is in Ready /
    ///     Review / Pending / Processing / Sending). This enforces the rule
    ///     "the user must validate every screenshot before shipping the
    ///     batch" specified in the smart-split plan.
    /// </summary>
    private bool CanSendBatch()
        => OnSendBatchRequested is not null
           && !IsBatchInProgress
           && ValidatedCount > 0
           && PendingCount == 0;

    /// <summary>
    /// Triggers the smart-split + sequential POST flow for every Validated
    /// item. The actual planning + dialog + POSTs are owned by
    /// <see cref="IBatchPlanner"/> + <see cref="IBatchSubmitter"/>; this
    /// command just snapshots the eligible items and delegates.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSendBatch))]
    private async Task SendBatchAsync()
    {
        if (OnSendBatchRequested is null) return;

        var validated = Items.Where(i => i.Status == InboxStatus.Validated).ToList();
        if (validated.Count == 0) return;

        BeginBatch();
        try
        {
            await OnSendBatchRequested(validated, _batchCts!.Token).ConfigureAwait(true);
        }
        finally
        {
            EndBatch();
        }
    }

    /// <summary>Re-arms a Send batch run on the current Failed items only.
    /// Useful after a batch where some items hit a transient HTTP 500 that
    /// the auto-retry couldn't ride out — the user fixes connectivity and
    /// hits this once.</summary>
    [RelayCommand(CanExecute = nameof(CanRetryFailed))]
    private async Task RetryFailedAsync()
    {
        if (OnSendBatchRequested is null) return;

        // Move every Failed item back to Validated so the planner picks them
        // up. Their drafts are intact (we never wipe them on Failed) so the
        // payloads will be the same as the failed attempt.
        var failed = Items.Where(i => i.Status == InboxStatus.Failed).ToList();
        if (failed.Count == 0) return;
        foreach (var f in failed)
        {
            f.Status = InboxStatus.Validated;
            f.StatusReason = "Re-queued for retry.";
        }

        BeginBatch();
        try
        {
            await OnSendBatchRequested(failed, _batchCts!.Token).ConfigureAwait(true);
        }
        finally
        {
            EndBatch();
        }
    }

    private bool CanRetryFailed()
        => OnSendBatchRequested is not null
           && !IsBatchInProgress
           && FailedCount > 0;

    /// <summary>
    /// Cancels the in-flight batch. The submitter finishes the current POST
    /// (we never abort a request mid-flight to keep the UEX server-side
    /// state coherent with our local history) and skips every subsequent
    /// item. The skipped items remain in <see cref="InboxStatus.Validated"/>
    /// so the user can hit Send batch again later.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStopBatch))]
    private void StopBatch()
    {
        if (_batchCts is not null && !_batchCts.IsCancellationRequested)
            _batchCts.Cancel();
    }

    private bool CanStopBatch() => IsBatchInProgress;

    private void BeginBatch()
    {
        _batchCts?.Dispose();
        _batchCts = new CancellationTokenSource();
        IsBatchInProgress = true;
    }

    private void EndBatch()
    {
        _batchCts?.Dispose();
        _batchCts = null;
        IsBatchInProgress = false;
        RecomputeBatchCounters();
    }

    /// <summary>
    /// Called by the editor right after a successful Validate click so the
    /// inbox can advance to the next non-validated item without the user
    /// having to manually click each one. Picks the next item by ascending
    /// <see cref="InboxItem.QueuePosition"/>; wraps at the end (and skips
    /// items in terminal / processing states).
    ///
    /// Behaviour decision: when nothing else needs validating, we DO NOT
    /// move the selection — the user stays on the current (now Validated)
    /// item with the green banner showing, ready to either edit again or
    /// click Send batch in the toolbar.
    /// </summary>
    public void SelectNextNonValidatedAfter(InboxItem current)
    {
        if (current is null) return;
        var startIndex = Items.IndexOf(current);
        if (startIndex < 0) return;

        // Walk forward then wrap. We use a single iteration over Items.Count
        // entries to guarantee termination even if the current item is the
        // last one (or the only one).
        for (var step = 1; step <= Items.Count; step++)
        {
            var idx = (startIndex + step) % Items.Count;
            var candidate = Items[idx];
            if (ReferenceEquals(candidate, current)) continue;
            if (candidate.Status is InboxStatus.Ready or InboxStatus.Review)
            {
                SelectedItem = candidate;
                SelectedItems.Clear();
                SelectedItems.Add(candidate);
                return;
            }
        }
        // Nothing left to validate → keep the current selection (the user
        // sees the validated banner with the option to Edit again or send).
    }
}

public sealed partial class InboxItem : ObservableObject
{
    /// <summary>
    /// 1-based position in the inbox queue (updated whenever <see cref="InboxViewModel.Items"/>
    /// changes). Bound in the inbox card as a compact "#N" badge so multiple
    /// screenshots are visually distinct.
    /// </summary>
    [ObservableProperty] private int _queuePosition;

    [ObservableProperty] private string _imagePath = "";
    [ObservableProperty] private string _displayName = "";

    /// <summary>
    /// Lifecycle state of the item in the inbox queue. Drives the card's
    /// status badge color, the editor's auto-open behaviour, and the batch
    /// planner's eligibility check (only <see cref="InboxStatus.Validated"/>
    /// items are picked up by Send batch).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsValidated))]
    [NotifyPropertyChangedFor(nameof(IsSending))]
    [NotifyPropertyChangedFor(nameof(IsTerminal))]
    private InboxStatus _status = InboxStatus.Pending;

    /// <summary>True iff the user has explicitly clicked Validate on this item.
    /// Bound by the inbox card's "validated" check icon. Mirror of
    /// <see cref="Status"/>=<see cref="InboxStatus.Validated"/>.</summary>
    public bool IsValidated => Status == InboxStatus.Validated;

    /// <summary>True iff the item is currently being POSTed to UEX. Drives a
    /// per-card spinner / "Sending..." label during a batch run.</summary>
    public bool IsSending => Status == InboxStatus.Sending;

    /// <summary>Terminal states (Sent / Failed) — bound to slightly stronger
    /// card tints so the user sees at a glance which items have been
    /// processed vs. which are still in the queue.</summary>
    public bool IsTerminal => Status is InboxStatus.Sent or InboxStatus.Failed;

    [ObservableProperty] private DateTimeOffset _addedAt;
    [ObservableProperty] private string? _terminalLabel;
    [ObservableProperty] private int _rowCount;
    [ObservableProperty] private string? _statusReason;

    /// <summary>
    /// Star Citizen branch the screenshot originates from, determined by the
    /// watcher slot that picked up the file (LIVE folder vs PTU folder).
    /// Drives the inbox card's coloured branch badge and is propagated into
    /// the <see cref="ParsedSubmission.Branch"/> when OCR completes so the
    /// submission carries the right <c>game_version</c>.
    /// </summary>
    [ObservableProperty] private GameBranch _branch = GameBranch.Live;

    /// <summary>True for items that came from the PTU watcher slot. Bound by
    /// the inbox card to swap the badge style (green LIVE vs orange PTU).</summary>
    public bool IsPtu => Branch == GameBranch.Ptu;

    partial void OnBranchChanged(GameBranch value) => OnPropertyChanged(nameof(IsPtu));

    // ---- Editor draft fields ----
    // The form-side state that doesn't map back into ParsedSubmission lives
    // on the InboxItem itself so the user's edits survive across selection
    // changes (Inbox → another item → back), navigation (Inbox → Settings →
    // Inbox), and even app restarts (when persisted in a future pass).
    // ScreenshotEditViewModel.SaveDraftToBoundItem() writes here; Load()
    // hydrates from here when present, falling back to the OCR-only
    // ParsedSubmission otherwise.

    /// <summary>User-overridden game version for this item (populated when
    /// the user typed something different from the auto-resolved LIVE/PTU
    /// build). <c>null</c> means "use the resolved default at submit time".</summary>
    public string? DraftGameVersion { get; set; }

    /// <summary>Free-form notes typed by the user in the DETAILS field.</summary>
    public string? DraftDetails { get; set; }

    /// <summary>True when the user has explicitly toggled the per-item
    /// production override after Load() — overrides the global
    /// <c>DefaultIsProduction</c> preference for this item only. <c>null</c>
    /// means "follow the current global preference".</summary>
    public bool? DraftIsProduction { get; set; }

    /// <summary>True when the user clicked an item in the terminal dropdown
    /// (or otherwise made an explicit choice) to disambiguate a name that
    /// exists in multiple star systems. Persists the ambiguity-confirmation
    /// flag across selection changes so the user doesn't have to re-confirm
    /// when they come back to an item they already disambiguated.</summary>
    public bool DraftUserExplicitlyConfirmedTerminal { get; set; }

    /// <summary>True when the user ticked the "I have reviewed everything"
    /// override checkbox to bypass blocking validation errors. Persisted on
    /// the item so navigating away and back doesn't silently re-arm the
    /// blocking gate.</summary>
    public bool DraftUserOverrideValidation { get; set; }

    /// <summary>True once <see cref="ScreenshotEditViewModel.SaveDraftToBoundItem"/>
    /// has run at least once for this item, ie. the user has touched the
    /// editor. Used so <see cref="ScreenshotEditViewModel.Load"/> knows
    /// whether to honour the draft (resume edits) or treat the item as
    /// fresh (use the OCR result + global defaults).</summary>
    public bool HasDraft { get; set; }

    /// <summary>
    /// Strips the redundant "ScreenShot-" prefix and file extension from a Star
    /// Citizen screenshot file name to get a compact display label that fits in
    /// the inbox card without truncating the status text.
    ///
    /// Examples:
    ///   "ScreenShot-2026-05-02_22-58-28-DB6.jpg" -> "2026-05-02 22:58:28"
    ///   "ScreenShot-2026-05-02_22-58-28.jpg"     -> "2026-05-02 22:58:28"
    /// Falls back to the raw file name (without extension) for non-SC files.
    /// </summary>
    public static string FormatDisplayName(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        var name = System.IO.Path.GetFileNameWithoutExtension(path);

        // SC pattern: ScreenShot-YYYY-MM-DD_HH-MM-SS[-XXX]
        // The trailing "-XXX" is a 3-character hex tag (eg. "DB6", "8AF") that
        // some SC builds append after the timestamp. It's optional — we can't
        // assume it's there.
        const string scPrefix = "ScreenShot-";
        if (name.StartsWith(scPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var rest = name[scPrefix.Length..];

            // Strip the trailing hex tag ONLY when it really looks like one.
            // Naively cutting at the last '-' would also eat the "-SS" seconds
            // segment for filenames that have no hex tag (truncating
            // "2026-05-02_22-58-28" to "2026-05-02_22-58" -> "2026-05-02 22:58").
            // The hex tag is 3-4 chars long AND contains at least one letter
            // (the timestamp pieces are always pure digits), so we use that to
            // distinguish them.
            var lastDash = rest.LastIndexOf('-');
            if (lastDash > 0 && lastDash < rest.Length - 1)
            {
                var tail = rest[(lastDash + 1)..];
                if (tail.Length is 3 or 4 && tail.Any(char.IsLetter))
                {
                    rest = rest[..lastDash];
                }
            }

            var underscore = rest.IndexOf('_');
            if (underscore > 0 && underscore < rest.Length - 1)
            {
                var date = rest[..underscore];
                var time = rest[(underscore + 1)..].Replace('-', ':');
                return $"{date} {time}";
            }
            return rest;
        }

        return name;
    }

    private System.Windows.Media.Imaging.BitmapImage? _thumbnail;

    /// <summary>
    /// Lazy-loaded 96px-wide thumbnail used in the inbox card. Built on demand
    /// from <see cref="ImagePath"/> the FIRST time it's accessed (typically when
    /// the card binds to it), then cached in memory until the item is removed.
    /// We use <see cref="System.Windows.Media.Imaging.BitmapCacheOption.OnLoad"/>
    /// + <c>DecodePixelWidth = 96</c> so the file handle is released immediately
    /// and memory stays under ~30 KB per thumbnail.
    /// </summary>
    public System.Windows.Media.Imaging.BitmapImage? Thumbnail
    {
        get
        {
            if (_thumbnail is not null) return _thumbnail;
            if (string.IsNullOrWhiteSpace(ImagePath) || !System.IO.File.Exists(ImagePath))
                return null;
            try
            {
                var bmp = new System.Windows.Media.Imaging.BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = 96;
                bmp.UriSource = new Uri(ImagePath);
                bmp.EndInit();
                bmp.Freeze();
                _thumbnail = bmp;
                return _thumbnail;
            }
            catch
            {
                return null;
            }
        }
    }

    public ParsedSubmission? Submission { get; set; }
    public UexDataSubmitPayload? Payload { get; set; }

    /// <summary>
    /// Full file paths of EVERY screenshot represented by this item. For a regular
    /// single-shot import this is just <c>[ImagePath]</c>. For a merged item it
    /// holds all the source paths so the watcher can skip them at rescan and the
    /// post-send cleanup can delete them all.
    ///
    /// Always non-null. The first entry is conventionally the "primary" (preview
    /// shown in the editor / sent to UEX in the `screenshot` field).
    /// </summary>
    public List<string> SourcePaths { get; set; } = new();

    /// <summary>Convenience: just the file names (basenames) of <see cref="SourcePaths"/>.</summary>
    public List<string> SourceFileNames => SourcePaths
        .Select(System.IO.Path.GetFileName)
        .Where(n => !string.IsNullOrWhiteSpace(n))
        .Select(n => n!)
        .ToList();
}

public enum InboxStatus
{
    Pending,
    Processing,
    Ready,
    Review,
    /// <summary>
    /// User has explicitly confirmed the OCR result and locked the editor.
    /// The item is queued for the next batch send. Set ONLY by an explicit
    /// click on Validate; auto-cleared back to <see cref="Ready"/> /
    /// <see cref="Review"/> as soon as any field changes (defence in depth
    /// against silent edits between Validate and Send batch).
    /// </summary>
    Validated,
    /// <summary>
    /// Currently being POSTed to UEX as part of an in-flight batch (or a
    /// "Send now" unitary submission). Transient: cleared to
    /// <see cref="Sent"/> or <see cref="Failed"/> as soon as the call
    /// returns.
    /// </summary>
    Sending,
    Sent,
    Failed,
}
