using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
    /// via a behavior so we can drive Merge / RemoveSelected commands without
    /// relying on ListBox.SelectedItems (which is read-only on the binding side).
    /// </summary>
    public ObservableCollection<InboxItem> SelectedItems { get; } = new();

    [ObservableProperty] private InboxItem? _selectedItem;
    [ObservableProperty] private ScreenshotEditViewModel? _currentEditor;

    /// <summary>
    /// Drives both the visibility AND the IsEnabled state of the Merge button.
    ///
    /// IMPORTANT: <c>NotifyCanExecuteChangedFor</c> is REQUIRED for
    /// <c>[RelayCommand(CanExecute = ...)]</c> to re-evaluate when this bool
    /// flips. Without it, the toolkit only checks CanExecute on command
    /// construction and never again — leading to the bug where the button
    /// stays grayed even though the underlying property is true.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MergeSelectedCommand))]
    private bool _canMergeSelected;

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

    public InboxViewModel()
    {
        SelectedItems.CollectionChanged += OnSelectedItemsChanged;
    }

    private void OnSelectedItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Merge requires AT LEAST 2 items, all of which must have a successful
        // ParsedSubmission with a detected terminal. We don't gate on terminal
        // matching here — the merge command itself reports a friendly error
        // if the terminals differ.
        var eligible = SelectedItems.Count(i =>
            i.Submission is not null &&
            i.Submission.IdTerminal is not null);
        CanMergeSelected = eligible >= 2;
    }

    partial void OnSelectedItemChanged(InboxItem? value)
    {
        if (value is null)
        {
            CurrentEditor = null;
            return;
        }

        var editor = App.Resolve<ScreenshotEditViewModel>();
        editor.Load(value);
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
                    DisplayName = Path.GetFileName(path),
                    Status = InboxStatus.Pending,
                    AddedAt = DateTimeOffset.Now,
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

    [RelayCommand]
    private void RemoveSelected()
    {
        if (SelectedItem is null) return;
        var toRemove = SelectedItem;
        // Reset selection BEFORE removing so the editor is dropped immediately
        // (the ListBox will also reset its own SelectedItem after removal but
        // we want to be defensive here against any binding-update race).
        SelectedItem = null;
        CurrentEditor = null;
        Items.Remove(toRemove);
    }

    /// <summary>
    /// Merges 2+ selected screenshots of the SAME terminal into a single
    /// consolidated InboxItem. Commodity rows are deduplicated by id_commodity
    /// (later submissions override earlier ones for the same commodity, so the
    /// most recent screenshot wins).
    ///
    /// The source items are kept in the inbox (status set to <see cref="InboxStatus.Sent"/>
    /// is NOT applied — they keep their original state) so the user can split
    /// the merge later if needed. The merged item replaces the selection.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanMergeSelected))]
    private void MergeSelected()
    {
        var sources = SelectedItems
            .Where(i => i.Submission is not null && i.Submission.IdTerminal is not null)
            .OrderBy(i => i.AddedAt)
            .ToList();

        if (sources.Count < 2) return;

        // Group by detected terminal id. We allow merging only if all items
        // share the same terminal — otherwise the user is mixing two real
        // visits and probably doesn't want them collapsed into one submission.
        var distinctTerminals = sources
            .Select(s => s.Submission!.IdTerminal!.Value)
            .Distinct()
            .ToList();

        if (distinctTerminals.Count > 1)
        {
            App.Resolve<Services.IDialogService>().ShowError(
                "Cannot merge — different terminals",
                $"The selected screenshots reference {distinctTerminals.Count} different terminals. " +
                "Only screenshots of the SAME terminal can be merged into one submission.");
            return;
        }

        // Deduplicate rows: later screenshots win over earlier ones for the
        // same commodity. This matches user intent — they re-screened to
        // refresh data, the latest values are the truth.
        var mergedRows = new Dictionary<int, ParsedPriceRow>();
        foreach (var src in sources)
        {
            foreach (var row in src.Submission!.Prices)
            {
                if (row.IdCommodity is null) continue;
                mergedRows[row.IdCommodity.Value] = row;
            }
        }

        var first = sources[0];
        var mergedSubmission = new ParsedSubmission
        {
            SourceImage = first.Submission!.SourceImage,
            Type = first.Submission.Type,
            IsProduction = first.Submission.IsProduction,
            Tab = first.Submission.Tab,
            IdTerminal = first.Submission.IdTerminal,
            TerminalDisplayName = first.Submission.TerminalDisplayName,
            TerminalMatchScore = first.Submission.TerminalMatchScore,
            TerminalMatchedFromOcr = first.Submission.TerminalMatchedFromOcr,
            TerminalMatchedField = first.Submission.TerminalMatchedField,
            ContainerSizes = first.Submission.ContainerSizes,
            Prices = mergedRows.Values.ToList(),
            NeedsReview = sources.SelectMany(s => s.Submission!.NeedsReview).Distinct().ToList(),
            Notes = $"Merged from {sources.Count} screenshots ({string.Join(", ", sources.Select(s => Path.GetFileName(s.ImagePath)))})",
        };

        var mergedItem = new InboxItem
        {
            ImagePath = first.ImagePath, // Primary screenshot for preview / attachment
            DisplayName = $"[Merged x{sources.Count}] {first.Submission.TerminalDisplayName}",
            Status = InboxStatus.Review,
            StatusReason = $"Merged {mergedRows.Count} commodities from {sources.Count} screenshots",
            AddedAt = DateTimeOffset.Now,
            TerminalLabel = first.Submission.TerminalDisplayName,
            RowCount = mergedRows.Count,
            Submission = mergedSubmission,
        };

        Items.Add(mergedItem);
        SelectedItems.Clear();
        SelectedItem = mergedItem;
    }
}

public sealed partial class InboxItem : ObservableObject
{
    [ObservableProperty] private string _imagePath = "";
    [ObservableProperty] private string _displayName = "";
    [ObservableProperty] private InboxStatus _status = InboxStatus.Pending;
    [ObservableProperty] private DateTimeOffset _addedAt;
    [ObservableProperty] private string? _terminalLabel;
    [ObservableProperty] private int _rowCount;
    [ObservableProperty] private string? _statusReason;

    public ParsedSubmission? Submission { get; set; }
    public UexDataSubmitPayload? Payload { get; set; }
}

public enum InboxStatus
{
    Pending,
    Processing,
    Ready,
    Review,
    Sent,
    Failed,
}
