using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
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

        // Resolve the rich label (Name · System) for the merged item so the inbox
        // card shows the disambiguated terminal, not just the bare name.
        var mergedTerminal = first.Submission.IdTerminal is { } tid
            ? App.Resolve<DataRunner.Core.Abstractions.ICatalogProvider>().GetTerminal(tid)
            : null;
        var mergedRichLabel = mergedTerminal?.RichDisplayName ?? first.Submission.TerminalDisplayName;

        var mergedItem = new InboxItem
        {
            ImagePath = first.ImagePath, // Primary screenshot for preview / attachment
            DisplayName = $"[Merged x{sources.Count}] {mergedRichLabel}",
            Status = InboxStatus.Review,
            StatusReason = $"Merged {mergedRows.Count} commodities from {sources.Count} screenshots",
            AddedAt = DateTimeOffset.Now,
            TerminalLabel = mergedRichLabel,
            RowCount = mergedRows.Count,
            Submission = mergedSubmission,
            // Track ALL sources so the post-send cleanup can delete every file
            // and the rescan filter can skip every basename. Without this the
            // 2 stragglers from the merge would be re-OCRed at the next scan.
            SourcePaths = sources.Select(s => s.ImagePath).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
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
    Sent,
    Failed,
}
