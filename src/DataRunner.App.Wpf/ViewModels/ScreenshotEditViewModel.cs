using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DataRunner.App.Services;
using DataRunner.App.ViewModels.Validation;
using DataRunner.Core.Abstractions;
using DataRunner.Core.Models;
using DataRunner.UexClient;
using Microsoft.Extensions.Logging;

namespace DataRunner.App.ViewModels;

/// <summary>
/// The validation/edit form. Lets the user fix the OCR result before submitting.
/// All user-edits flow back into the bound <see cref="UexDataSubmitPayload"/>.
/// </summary>
public sealed partial class ScreenshotEditViewModel : ObservableObject
{
    private readonly ICatalogProvider _catalog;
    private readonly IPayloadValidator _validator;
    private readonly IDuplicateChecker _dupChecker;
    private readonly IUexApiClient _api;
    private readonly ISubmissionHistory _history;
    private readonly IAppPreferences _prefs;
    private readonly IDialogService _dialog;
    private readonly IGameVersionsService _gameVersions;
    private readonly OcrCoordinator _ocr;
    private readonly ILogger<ScreenshotEditViewModel> _logger;

    /// <summary>UEX hard limit for the `screenshot` field (10 MB raw → ~13.4 MB base64).</summary>
    private const long MaxScreenshotBytes = 10L * 1024 * 1024;

    private InboxItem? _bound;
    private bool _suspendSearchSync;

    [ObservableProperty] private BitmapImage? _previewImage;
    [ObservableProperty] private string _sourceImagePath = "";
    [ObservableProperty] private bool _isProduction;

    /// <summary>
    /// True when the bound inbox item was built by merging 2+ separate
    /// screenshots (Inbox → "Merge selected"). The view shows a warning
    /// banner because UEX accepts only ONE screenshot per /data_submit POST,
    /// so only the first source image is attached. New datarunners (90-day
    /// evaluation) may see the submission rejected for rows whose visual
    /// evidence isn't on that one attached screenshot.
    /// </summary>
    public bool IsMergedItem => _bound?.SourcePaths?.Count > 1;

    /// <summary>How many source screenshots compose the bound item.</summary>
    public int MergedSourceCount => _bound?.SourcePaths?.Count ?? 1;

    /// <summary>
    /// True when the editor renders the source screenshot in a docked panel
    /// to the right of the validation form. Persisted to <see cref="IAppPreferences"/>
    /// so the user's layout choice survives across sessions. The view also
    /// uses min-width thresholds to auto-disable the side panel on narrow
    /// screens (so the form stays usable on a 1366×768 laptop).
    /// </summary>
    [ObservableProperty] private bool _sideBySideScreenshot;

    partial void OnSideBySideScreenshotChanged(bool value)
    {
        if (_prefs is null) return;
        _prefs.SideBySideScreenshot = value;
        _ = _prefs.SaveAsync();
    }

    [RelayCommand]
    private void ToggleSideBySide() => SideBySideScreenshot = !SideBySideScreenshot;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedTerminal))]
    private UexTerminal? _selectedTerminal;
    [ObservableProperty] private string _terminalSearch = "";

    /// <summary>
    /// True when a catalog terminal is bound to the form. Drives the
    /// "Bound: ..." confirmation banner under the terminal search box so
    /// the user can SEE which terminal id_terminal will carry without
    /// inspecting the payload — fixes issue #3 point 4.
    /// </summary>
    public bool HasSelectedTerminal => SelectedTerminal is not null;
    [ObservableProperty] private bool _isTerminalDropDownOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTabUndetected))]
    private TerminalTab _tab = TerminalTab.Buy;

    /// <summary>
    /// True when the OCR's colour-based tab detector couldn't decide
    /// BUY vs SELL. Drives the bright warning banner in the editor that
    /// directs the user to pick the correct side manually before the
    /// Send button unlocks. Submitting with the wrong side mirrors the
    /// price into the wrong UEX column — see the 2026-05-05 Pyro
    /// Gateway incident report in
    /// <see cref="HydrateFrom"/>.
    /// </summary>
    public bool IsTabUndetected => Tab == TerminalTab.Unknown;

    [ObservableProperty] private string _containerSizes = "";
    [ObservableProperty] private string _gameVersion = "";
    [ObservableProperty] private string _details = "";

    /// <summary>
    /// Star Citizen game branch the screenshot was taken from, derived from
    /// the watcher slot (LIVE folder vs PTU folder). Auto-resolves the
    /// matching <c>game_version</c> string (e.g. "4.7.2" or "4.8.0") via
    /// <see cref="IGameVersionsService"/> when the user hasn't typed a
    /// custom value in the GAME VERSION field.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BranchLabel))]
    [NotifyPropertyChangedFor(nameof(IsPtuBranch))]
    private GameBranch _branch = GameBranch.Live;

    /// <summary>True for PTU screenshots; the editor surfaces a small banner
    /// noting that UEX may temporarily reject PTU reports.</summary>
    public bool IsPtuBranch => Branch == GameBranch.Ptu;

    /// <summary>
    /// Read-only label shown next to the GAME VERSION input. Tells the user
    /// which branch the screenshot was tagged with by the watcher and what
    /// resolved <c>game_version</c> string the payload will carry by default.
    /// Updated whenever <see cref="Branch"/> changes or after a /game_versions
    /// fetch lands.
    /// </summary>
    public string BranchLabel
    {
        get
        {
            var c = _gameVersions?.Cached;
            var resolved = Branch == GameBranch.Ptu
                ? (string.IsNullOrWhiteSpace(c?.Ptu) ? "PTU" : c!.Ptu!)
                : (string.IsNullOrWhiteSpace(c?.Live) ? "LIVE" : c!.Live!);
            var branchName = Branch == GameBranch.Ptu ? "PTU" : "LIVE";
            return $"{branchName} screenshot · auto-fills game_version with \"{resolved}\" (override below to change)";
        }
    }

    [ObservableProperty] private double _terminalMatchScore;
    [ObservableProperty] private string _terminalSourceField = "";
    [ObservableProperty] private string _terminalFromOcr = "";

    /// <summary>
    /// Source screenshot width / height in pixels, captured by the OCR
    /// pipeline. 0 when unknown (eg. legacy submissions or text-only
    /// fixtures). Used by <see cref="RecomputeValidation"/> to warn the
    /// user when the image was resized to a non-standard aspect ratio
    /// — small-glyph regions (terminal name in the LEFT panel) become
    /// unreliable below ~800px height regardless of compression
    /// settings, and that degradation is invisible without an explicit
    /// hint.
    /// </summary>
    [ObservableProperty] private int _sourceImageWidth;
    [ObservableProperty] private int _sourceImageHeight;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReRunOcrCommand))]
    private bool _canReRunOcr;

    /// <summary>
    /// True if <see cref="SelectedTerminal"/> shares its display name with another
    /// terminal in a different star system. UI surfaces a warning + inline picker.
    /// </summary>
    [ObservableProperty] private bool _isAmbiguousTerminal;

    /// <summary>
    /// Whether the user has explicitly picked / confirmed the terminal in the
    /// dropdown (versus inheriting whatever the OCR guessed). Only matters when
    /// the terminal name is ambiguous — in which case Send is gated until the
    /// user confirms even by re-clicking the same one. Reset on every OCR
    /// re-run / load so a fresh OCR guess is never silently trusted.
    /// </summary>
    [ObservableProperty] private bool _userExplicitlyConfirmedTerminal;

    /// <summary>List of candidate terminals when the picked name is ambiguous (count ≥ 2).</summary>
    public ObservableCollection<UexTerminal> AmbiguousCandidates { get; } = new();

    /// <summary>
    /// Live (incremental) validation issues — recomputed whenever any user-editable
    /// field changes. Drives the sticky validation footer AND the Send button gate.
    /// </summary>
    public ObservableCollection<LiveValidationIssue> ValidationIssues { get; } = new();

    [ObservableProperty] private int _errorCount;
    [ObservableProperty] private int _warningCount;

    /// <summary>True when at least one validation Error is present. Disables Send
    /// unless <see cref="UserOverrideValidation"/> is also true.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    private bool _hasBlockingErrors;

    /// <summary>
    /// Manual override the user can tick when blocking errors are present to
    /// confirm "I have reviewed every value myself, send it anyway". Reset to
    /// false on every <see cref="Load"/> so the safety net stays active by
    /// default for each new screenshot.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    private bool _userOverrideValidation;

    /// <summary>Concise summary line for the footer (e.g. "2 errors · 1 warning").</summary>
    [ObservableProperty] private string _validationSummary = "";

    /// <summary>
    /// Overall severity name for the validation panel — feeds <c>SeverityToBrushConverter</c>.
    /// "Error" if any error, "Warning" if any warning only, "Ok" if all clean.
    /// </summary>
    [ObservableProperty] private string _overallSeverity = "Ok";

    public ObservableCollection<EditableRow> Rows { get; } = new();
    public ObservableCollection<UexTerminal> TerminalSuggestions { get; } = new();
    public ObservableCollection<UexCommodity> CommodityOptions { get; } = new();

    public ScreenshotEditViewModel(
        ICatalogProvider catalog,
        IPayloadValidator validator,
        IDuplicateChecker dupChecker,
        IUexApiClient api,
        ISubmissionHistory history,
        IAppPreferences prefs,
        IDialogService dialog,
        IGameVersionsService gameVersions,
        OcrCoordinator ocr,
        ILogger<ScreenshotEditViewModel> logger)
    {
        _catalog = catalog;
        _validator = validator;
        _dupChecker = dupChecker;
        _api = api;
        _history = history;
        _prefs = prefs;
        _dialog = dialog;
        _gameVersions = gameVersions;
        _ocr = ocr;
        _logger = logger;

        foreach (var c in _catalog.Commodities.OrderBy(c => c.Name))
            CommodityOptions.Add(c);

        // Hydrate persisted layout preference. We set the BACKING FIELD directly
        // (not the property) to avoid the OnSideBySideScreenshotChanged partial
        // method firing during construction, which would write the value back to
        // prefs.json for no reason. PropertyChanged is raised manually so any
        // bound view sees the initial value.
        _sideBySideScreenshot = _prefs.SideBySideScreenshot;
        OnPropertyChanged(nameof(SideBySideScreenshot));

        // Live validation: re-run on every relevant change. We hook three sources:
        //   - the Rows collection itself (add/remove)
        //   - each row's PropertyChanged (commodity / scu / price / status)
        //   - the top-level header fields (terminal, tab) handled by partial methods below.
        Rows.CollectionChanged += OnRowsCollectionChanged;
        RecomputeValidation();
    }

    private void OnRowsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (EditableRow r in e.OldItems) r.PropertyChanged -= OnAnyRowPropertyChanged;
        if (e.NewItems is not null)
            foreach (EditableRow r in e.NewItems) r.PropertyChanged += OnAnyRowPropertyChanged;
        RecomputeValidation();
    }

    private void OnAnyRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Skip the cosmetic-only computed properties to avoid infinite recursion.
        if (e.PropertyName is nameof(EditableRow.MatchSeverity)
            or nameof(EditableRow.ScuIsEmpty)
            or nameof(EditableRow.PriceIsEmpty)
            or nameof(EditableRow.CommodityIsMissing))
        {
            return;
        }
        RecomputeValidation();
    }

    partial void OnSelectedTerminalChanged(UexTerminal? value)
    {
        // Always recompute ambiguity / candidates regardless of who triggered the
        // change (OCR hydration vs user click), so the warning UI is consistent.
        UpdateAmbiguityState(value);

        if (_suspendSearchSync || value is null)
        {
            // Hydration path (OCR pre-fill or .Load()). We do NOT mark this as
            // a user confirmation: if the terminal is ambiguous, the user must
            // still pick one explicitly before Send is unlocked.
            RecomputeValidation();
            return;
        }

        // Real user pick from the dropdown.
        UserExplicitlyConfirmedTerminal = true;

        _suspendSearchSync = true;
        // Surface the FULL hierarchy (shop · station · system) in the search
        // box so the user sees exactly which terminal is bound, not just the
        // shared parent-station name. Critical for stations like Nyx Gateway
        // where multiple sibling shops collapse to the same DisplayName.
        TerminalSearch = value.RichDisplayName;
        _suspendSearchSync = false;
        IsTerminalDropDownOpen = false;
        RecomputeValidation();
    }

    private void UpdateAmbiguityState(UexTerminal? value)
    {
        AmbiguousCandidates.Clear();
        if (value is null)
        {
            IsAmbiguousTerminal = false;
            return;
        }
        IsAmbiguousTerminal = _catalog.IsAmbiguous(value);
        if (!IsAmbiguousTerminal) return;

        var key = !string.IsNullOrWhiteSpace(value.DisplayName) ? value.DisplayName : value.Name;
        foreach (var t in _catalog.CommodityTerminals
                     .Where(t => string.Equals(
                         !string.IsNullOrWhiteSpace(t.DisplayName) ? t.DisplayName : t.Name,
                         key, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(t => t.StarSystemName))
        {
            AmbiguousCandidates.Add(t);
        }
    }

    partial void OnTabChanged(TerminalTab value) => RecomputeValidation();
    partial void OnTerminalMatchScoreChanged(double value) => RecomputeValidation();
    partial void OnUserExplicitlyConfirmedTerminalChanged(bool value) => RecomputeValidation();
    partial void OnIsAmbiguousTerminalChanged(bool value) => RecomputeValidation();
    partial void OnSourceImageWidthChanged(int value) => RecomputeValidation();
    partial void OnSourceImageHeightChanged(int value) => RecomputeValidation();

    /// <summary>
    /// Walks the current state and rebuilds the issue list. Cheap (sync, no I/O).
    /// Single source of truth for the Send-button gate AND the footer panel.
    /// </summary>
    private void RecomputeValidation()
    {
        ValidationIssues.Clear();

        if (SelectedTerminal is null)
        {
            // Distinguish "user hasn't typed anything yet" (default empty form)
            // from "user typed text that didn't resolve to a unique terminal".
            // The latter is the GitHub issue #3 pain point: the old generic
            // error left the user staring at the API rejection
            //   `id_terminal is required and must be a positive integer`
            // with no clue what to do. Surface the candidate count instead.
            var typed = (TerminalSearch ?? "").Trim();
            if (typed.Length == 0)
            {
                ValidationIssues.Add(new LiveValidationIssue
                {
                    Severity = LiveValidationSeverity.Error,
                    Code = "terminal_missing",
                    Message = "No terminal picked. Type a name in the TERMINAL field and pick one from the dropdown.",
                });
            }
            else
            {
                // Use the same matching logic as the dropdown so the count is
                // consistent with what the user sees.
                var candidateCount = CountCandidatesFor(typed);
                ValidationIssues.Add(new LiveValidationIssue
                {
                    Severity = LiveValidationSeverity.Error,
                    Code = "terminal_unresolved",
                    Message = candidateCount switch
                    {
                        0 => $"\"{typed}\" doesn't match any terminal in the catalog. Try a shorter or different spelling — the dropdown searches shop name, station, outpost, city and star system.",
                        _ => $"\"{typed}\" matches {candidateCount} terminals — pick one explicitly in the dropdown so the correct id_terminal is sent.",
                    },
                });
            }
        }
        else
        {
            // CRITICAL UEX QUALITY RULE: when the same terminal name exists in
            // multiple star systems (Pyro Gateway in Stanton vs Pyro, ARC-L1 vs
            // CRU-L1, etc.), the user MUST explicitly pick one — otherwise the
            // OCR's blind guess can pollute UEX with cross-system bad data.
            // See UEX community feedback: this is the #1 source of bad reports.
            if (IsAmbiguousTerminal && !UserExplicitlyConfirmedTerminal)
            {
                ValidationIssues.Add(new LiveValidationIssue
                {
                    Severity = LiveValidationSeverity.Error,
                    Code = "terminal_ambiguous_unconfirmed",
                    Message = $"\"{SelectedTerminal.DisplayName}\" exists in multiple star systems — pick the correct one in the dropdown (currently: {SelectedTerminal.RichDisplayName}).",
                });
            }

            if (TerminalMatchScore > 0 && TerminalMatchScore < 80)
            {
                ValidationIssues.Add(new LiveValidationIssue
                {
                    Severity = LiveValidationSeverity.Warning,
                    Code = "terminal_low_match",
                    Message = $"Terminal OCR match is only {TerminalMatchScore:0}% — double-check the terminal name.",
                });
            }
        }

        // Image-quality warning: when the source screenshot has a
        // non-standard aspect ratio (outside the 16:9..21:9 band) it has
        // almost certainly been cropped or resized after capture. SC's
        // commodity panel has fixed-relative-position UI elements and
        // small-glyph areas (terminal name in the LEFT panel under
        // YOUR INVENTORIES, status bands per row) become unreliable
        // when the source pixel density drops below ~1080p-equivalent.
        // Surfacing this as a Warning gives the user a heads-up to
        // verify EVERY field before sending — without blocking the
        // submission for users who knowingly work from edited captures.
        if (SourceImageWidth > 0 && SourceImageHeight > 0)
        {
            var ratio = (double)SourceImageWidth / SourceImageHeight;
            if (ratio is < 1.6 or > 2.4)
            {
                ValidationIssues.Add(new LiveValidationIssue
                {
                    Severity = LiveValidationSeverity.Warning,
                    Code = "image_aspect_unusual",
                    Message = $"Screenshot has an unusual aspect ratio ({SourceImageWidth}×{SourceImageHeight}, {ratio:0.00}:1) — looks resized or cropped. OCR confidence is degraded; verify every field carefully before sending.",
                });
            }
        }

        if (Rows.Count == 0)
        {
            ValidationIssues.Add(new LiveValidationIssue
            {
                Severity = LiveValidationSeverity.Error,
                Code = "no_rows",
                Message = "No commodity rows. Add at least one row before submitting.",
            });
        }

        // CRITICAL DATA-QUALITY GUARD: if the colour-based tab detector
        // could not commit to BUY or SELL (eg. low saturation gap on
        // amber-themed Pyro stations), refuse to submit until the user
        // makes an explicit pick. This blocks the silent Unknown → Buy
        // default that previously corrupted six Pyro Gateway SELL
        // captures into the UEX BUY column on 2026-05-05.
        if (Tab == TerminalTab.Unknown)
        {
            ValidationIssues.Add(new LiveValidationIssue
            {
                Severity = LiveValidationSeverity.Error,
                Code = "tab_unknown",
                Message = "Buy/Sell tab couldn't be auto-detected from the screenshot — pick the correct side in the TAB dropdown. Submitting with the wrong side pollutes UEX with mirrored prices.",
            });
        }

        // Per-row checks — produced in row order so the footer reads top-down.
        for (var i = 0; i < Rows.Count; i++)
        {
            var r = Rows[i];
            var label = r.Commodity?.Name ?? $"Row {i + 1}";

            if (r.CommodityIsMissing)
            {
                ValidationIssues.Add(new LiveValidationIssue
                {
                    Severity = LiveValidationSeverity.Error,
                    Code = "row_no_commodity",
                    Message = $"Row {i + 1}: no commodity selected.",
                    RowIndex = i,
                });
                continue;
            }

            // Strict blocking thresholds. The user can still bypass them by
            // explicitly ticking the "I have reviewed everything" override
            // checkbox in the validation footer (gated by UserOverrideValidation).
            //   < 85  -> Error (blocking unless override)
            //   85-99 -> Warning (non-blocking, prompt verification)
            //   100   -> silent
            if (r.MatchScore > 0 && r.MatchScore < 85)
            {
                ValidationIssues.Add(new LiveValidationIssue
                {
                    Severity = LiveValidationSeverity.Error,
                    Code = "row_match_too_low",
                    Message = $"{label}: OCR match too low ({r.MatchScore:0}%) — pick the correct commodity.",
                    RowIndex = i,
                });
            }
            else if (r.MatchScore > 0 && r.MatchScore < 100)
            {
                ValidationIssues.Add(new LiveValidationIssue
                {
                    Severity = LiveValidationSeverity.Warning,
                    Code = "row_match_imperfect",
                    Message = $"{label}: OCR match {r.MatchScore:0}% (not 100%) — verify the commodity name (OCR read: \"{r.FromOcr}\").",
                    RowIndex = i,
                });
            }

            if (r.ScuIsEmpty)
            {
                ValidationIssues.Add(new LiveValidationIssue
                {
                    Severity = LiveValidationSeverity.Error,
                    Code = "row_scu_missing",
                    Message = $"{label}: SCU value is missing.",
                    RowIndex = i,
                });
            }

            if (r.PriceIsEmpty)
            {
                ValidationIssues.Add(new LiveValidationIssue
                {
                    Severity = LiveValidationSeverity.Error,
                    Code = "row_price_missing",
                    Message = $"{label}: price is missing.",
                    RowIndex = i,
                });
            }

            // Inventory status MUST be set before submission. Sending /data_submit
            // without status_buy triggers UEX's `missing_inventory_status` rejection
            // (the row gets accepted by the API but flagged for manual review by
            // UEX staff, who then refuse the report). Auto-defaulting Unknown to
            // Maximum was tempting but would silently push wrong data into the
            // shared UEX dataset — block instead, force the user to pick one,
            // override checkbox available for the rare case where they want to
            // submit anyway.
            if (r.Status == InventoryStatus.Unknown)
            {
                ValidationIssues.Add(new LiveValidationIssue
                {
                    Severity = LiveValidationSeverity.Error,
                    Code = "row_status_unknown",
                    Message = $"{label}: inventory status is Unknown — pick the right one in the dropdown (Maximum / High / Medium / Low / Out of stock). UEX rejects submissions with missing status.",
                    RowIndex = i,
                });
            }
        }

        ErrorCount = ValidationIssues.Count(v => v.Severity == LiveValidationSeverity.Error);
        WarningCount = ValidationIssues.Count(v => v.Severity == LiveValidationSeverity.Warning);
        HasBlockingErrors = ErrorCount > 0;
        OverallSeverity = ErrorCount > 0 ? "Error" : (WarningCount > 0 ? "Warning" : "Ok");

        ValidationSummary = (ErrorCount, WarningCount) switch
        {
            (0, 0) => "Ready to submit.",
            (0, 1) => "1 warning — submission allowed.",
            (0, var w) => $"{w} warnings — submission allowed.",
            (1, 0) => "1 error blocks submission.",
            (var e, 0) => $"{e} errors block submission.",
            (1, 1) => "1 error · 1 warning — fix the error to enable Send.",
            (var e, var w) => $"{e} errors · {w} warnings — fix errors to enable Send.",
        };
    }

    /// <summary>
    /// Send is enabled when there are no blocking errors OR when the user has
    /// explicitly ticked the override checkbox to bypass the safety net.
    /// The override is per-screenshot and resets on every <see cref="Load"/>.
    /// </summary>
    private bool CanSubmit() => !HasBlockingErrors || UserOverrideValidation;

    /// <summary>
    /// Pre-fills the <see cref="GameVersion"/> field with the build number
    /// returned by /game_versions for the current <see cref="Branch"/>.
    /// Skips when the user has typed something already so we never clobber
    /// a manual override, even after a fresh OCR run on the same item.
    /// Refreshes the <see cref="BranchLabel"/> at the end so the read-only
    /// indicator next to the field matches what was assigned.
    /// </summary>
    private async Task ResolveAndAssignGameVersionAsync()
    {
        try
        {
            // Don't override a value the user typed (or that came back from
            // a previous load). The intent is a "smart default", not a
            // canonicaliser.
            if (!string.IsNullOrWhiteSpace(GameVersion))
            {
                OnPropertyChanged(nameof(BranchLabel));
                return;
            }

            var resolved = await _gameVersions.ResolveAsync(Branch).ConfigureAwait(true);
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                GameVersion = resolved!;
            }
            // Always refresh the branch label even when resolution failed,
            // so the user sees the literal "LIVE"/"PTU" fallback or a
            // friendly "no PTU build" hint.
            OnPropertyChanged(nameof(BranchLabel));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not pre-fill game_version for branch={Branch}", Branch);
        }
    }

    public void Load(InboxItem item)
    {
        _bound = item;
        CanReRunOcr = true;
        // Notify the view that the merged-item helpers may have changed: their
        // backing data (_bound.SourcePaths.Count) just changed but the toolkit
        // doesn't auto-detect that because they're computed properties.
        OnPropertyChanged(nameof(IsMergedItem));
        OnPropertyChanged(nameof(MergedSourceCount));
        // Each load resets the explicit-confirmation flags: the OCR's pre-pick
        // is a guess, not a commitment from the user. They must still confirm
        // (even if just by clicking the same terminal in the dropdown) when
        // the terminal name is ambiguous, and re-tick the override to bypass
        // any blocking validation errors on this new screenshot.
        UserExplicitlyConfirmedTerminal = false;
        UserOverrideValidation = false;
        // The production mode is a global preference now (Settings) — re-read
        // it on every load so the editor reflects the current global value
        // and the confirm dialog displays the right mode badge.
        IsProduction = _prefs.DefaultIsProduction;
        // Inherit the branch from the inbox item (set by the watcher slot the
        // screenshot was picked up from). Pre-fill the GAME VERSION field with
        // the resolved build number from /game_versions so the user sees the
        // exact value that will be sent and can override if they want.
        Branch = item.Branch;
        // Fire the resolution best-effort: if the cache misses we still hold
        // a sensible literal ("LIVE" / "PTU") via Resolve()'s fallback path.
        _ = ResolveAndAssignGameVersionAsync();
        SourceImagePath = item.ImagePath;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(item.ImagePath);
            bmp.EndInit();
            bmp.Freeze();
            PreviewImage = bmp;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load preview image: {Path}", item.ImagePath);
        }

        Rows.Clear();
        if (item.Submission is not null)
        {
            HydrateFrom(item.Submission);
        }
        else
        {
            // Empty form: let the user pick a terminal manually while OCR runs.
            _suspendSearchSync = true;
            SelectedTerminal = null;
            TerminalSearch = "";
            _suspendSearchSync = false;
            Tab = TerminalTab.Buy;
            ContainerSizes = "";
            TerminalMatchScore = 0;
            TerminalSourceField = "";
            TerminalFromOcr = "";
            SourceImageWidth = 0;
            SourceImageHeight = 0;
        }

        UpdateTerminalSuggestions();
    }

    private void HydrateFrom(ParsedSubmission s)
    {
        // CRITICAL: do NOT silently default Unknown → Buy. Doing so caused a
        // real-world data corruption incident at Pyro Gateway (2026-05-05):
        // the colour-based tab detector returned Unknown on six SELL captures
        // because Pyro's amber/orange theme produced a saturation gap below
        // the decision margin. The previous default sent SELL prices as BUY
        // prices to UEX without any warning. Keep Tab=Unknown here so the
        // ComboBox visibly shows "Unknown", RecomputeValidation raises a
        // BLOCKING error, and BuildPayload refuses to construct a payload —
        // forcing the user to make an explicit Buy/Sell pick.
        Tab = s.Tab;
        ContainerSizes = s.ContainerSizes ?? "";
        TerminalMatchScore = s.TerminalMatchScore;
        TerminalSourceField = s.TerminalMatchedField ?? "";
        TerminalFromOcr = s.TerminalMatchedFromOcr ?? "";
        SourceImageWidth = s.SourceImageWidth;
        SourceImageHeight = s.SourceImageHeight;
        // Branch may also have been populated by the OCR pipeline on the
        // ParsedSubmission directly (defensive — the watcher already set it
        // on the InboxItem in Load above); honour it when present.
        if (s.Branch != Branch) Branch = s.Branch;

        var resolved = s.IdTerminal is { } id ? _catalog.GetTerminal(id) : null;
        _suspendSearchSync = true;
        SelectedTerminal = resolved;
        TerminalSearch = resolved?.RichDisplayName ?? "";
        _suspendSearchSync = false;

        foreach (var row in s.Prices)
        {
            var commodity = row.IdCommodity is { } cid ? _catalog.GetCommodity(cid) : null;
            Rows.Add(new EditableRow
            {
                Commodity = commodity,
                ScuValue = row.ScuBuy,
                PriceValue = row.PriceBuy,
                Status = row.StatusBuy,
                MatchScore = row.CommodityMatchScore,
                FromOcr = row.CommodityMatchedFromOcr ?? "",
            });
        }
    }

    partial void OnTerminalSearchChanged(string value)
    {
        if (_suspendSearchSync) return;
        UpdateTerminalSuggestions();
        IsTerminalDropDownOpen = TerminalSuggestions.Count > 0;

        // Smart name → id resolution: when the typed query unambiguously
        // narrows the catalog down to a SINGLE terminal, treat it as a real
        // user pick. This is what unblocks the workflow where the user types
        // a fully-qualified label like "Platinum Bay - Nyx Gateway (Stanton)"
        // and would otherwise hit the cryptic
        //   `id_terminal is required and must be a positive integer`
        // payload error because no dropdown click ever fired.
        // We require ≥3 chars to avoid matching a random single token (e.g. "L1")
        // that happens to be unique in the catalog by accident.
        if (TerminalSuggestions.Count == 1
            && value is { Length: >= 3 })
        {
            var match = TerminalSuggestions[0];
            if (SelectedTerminal?.Id != match.Id)
            {
                _suspendSearchSync = true;
                SelectedTerminal = match;
                // Typing a string that resolves to exactly one terminal IS an
                // explicit choice — no need to also open the dropdown and
                // click the same item to lift the ambiguity gate.
                UserExplicitlyConfirmedTerminal = true;
                _suspendSearchSync = false;
            }
            return;
        }

        // Clear the resolved terminal if the user fully edited the text away
        // from the previous pick (otherwise BuildPayload would silently send
        // the previously-matched id). Compare against RichDisplayName since
        // that is what we display in the box on a successful pick.
        if (SelectedTerminal is not null
            && !string.Equals(SelectedTerminal.RichDisplayName, value, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(SelectedTerminal.DisplayName, value, StringComparison.OrdinalIgnoreCase))
        {
            _suspendSearchSync = true;
            SelectedTerminal = null;
            _suspendSearchSync = false;
        }

        // Refresh the validation panel: when the user types text that does
        // not resolve to any (or matches multiple) terminals, the
        // `terminal_unresolved` message must reflect the current candidate
        // count even though SelectedTerminal stayed null/unchanged.
        RecomputeValidation();
    }

    /// <summary>
    /// Tokens used to split a free-form terminal query like
    /// <c>"Platinum Bay - Nyx Gateway (Stanton)"</c> into matchable parts
    /// (<c>Platinum</c>, <c>Bay</c>, <c>Nyx</c>, <c>Gateway</c>, <c>Stanton</c>).
    /// Includes spaces, hyphens, mid-dots, slashes, parens and common
    /// punctuation so the same query works whether the user copies a
    /// canonical UEX label or just types loosely.
    /// </summary>
    private static readonly char[] TerminalQueryTokenSeparators =
        { ' ', '\t', '-', '–', '—', '·', '/', '\\', '(', ')', '[', ']', ',', '.', ':', ';', '|' };

    private void UpdateTerminalSuggestions()
    {
        TerminalSuggestions.Clear();

        IEnumerable<UexTerminal> source = _catalog.CommodityTerminals;
        var query = TerminalSearch?.Trim() ?? "";
        if (query.Length > 0)
        {
            // Tokenized AND search: every non-trivial token must hit at least
            // one searchable field. This is what lets users paste full canonical
            // labels (`"Platinum Bay - Nyx Gateway (Stanton)"`), use loose
            // ordering (`"stanton platinum nyx"`), or mix system + station.
            // Single-letter tokens are dropped to avoid pathological matches.
            var tokens = query
                .Split(TerminalQueryTokenSeparators, StringSplitOptions.RemoveEmptyEntries)
                .Where(t => t.Length >= 2)
                .ToArray();

            if (tokens.Length > 0)
            {
                source = source.Where(t => tokens.All(tok => MatchesAnyField(t, tok)));
            }
        }

        // When the currently-selected terminal is ambiguous, ALWAYS surface every
        // candidate sharing its name first — even if those don't match the search
        // string — so the user can disambiguate in one click without having to
        // scroll or re-type. This is the UEX community fix for the Gateway issue.
        var pinned = new List<UexTerminal>();
        var pinnedIds = new HashSet<int>();
        if (IsAmbiguousTerminal && AmbiguousCandidates.Count > 0)
        {
            foreach (var c in AmbiguousCandidates.OrderBy(t => t.StarSystemName)
                                                 .ThenBy(t => t.RichDisplayName))
            {
                pinned.Add(c);
                pinnedIds.Add(c.Id);
            }
        }

        foreach (var t in pinned) TerminalSuggestions.Add(t);
        foreach (var t in source.Where(t => !pinnedIds.Contains(t.Id))
                                 .OrderBy(t => t.RichDisplayName)
                                 .Take(50))
        {
            TerminalSuggestions.Add(t);
        }
    }

    /// <summary>
    /// Matches a single token (case-insensitive substring) against every
    /// searchable field of a terminal: shop name, display name, nickname,
    /// code, parent location (city / outpost / station / planet) and star
    /// system. Star system is intentionally included so a user typing
    /// <c>"stanton"</c> filters down to the right side of an ambiguous pair.
    /// </summary>
    private static bool MatchesAnyField(UexTerminal t, string token)
        => Contains(t.DisplayName, token)
        || Contains(t.Name, token)
        || Contains(t.Nickname, token)
        || Contains(t.Code, token)
        || Contains(t.SpaceStationName, token)
        || Contains(t.OutpostName, token)
        || Contains(t.CityName, token)
        || Contains(t.PlanetName, token)
        || Contains(t.StarSystemName, token);

    private static bool Contains(string? s, string q)
        => s is not null && s.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>
    /// Counts how many catalog terminals match a free-form query under the
    /// SAME tokenizer/matching rules as <see cref="UpdateTerminalSuggestions"/>.
    /// Used by validation to show the user a precise reason when their typed
    /// text doesn't resolve to a unique terminal.
    /// </summary>
    private int CountCandidatesFor(string query)
    {
        var tokens = (query ?? "")
            .Split(TerminalQueryTokenSeparators, StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length >= 2)
            .ToArray();
        if (tokens.Length == 0) return 0;
        return _catalog.CommodityTerminals
            .Count(t => tokens.All(tok => MatchesAnyField(t, tok)));
    }


    [RelayCommand]
    private void PickTerminal(UexTerminal? terminal)
    {
        if (terminal is null) return;
        SelectedTerminal = terminal;
        IsTerminalDropDownOpen = false;
    }

    [RelayCommand]
    private void AddRow() => Rows.Add(new EditableRow());

    [RelayCommand]
    private void RemoveRow(EditableRow? row)
    {
        if (row is not null) Rows.Remove(row);
    }

    [RelayCommand(CanExecute = nameof(CanReRunOcr))]
    private void ReRunOcr()
    {
        if (_bound is null) return;
        // A fresh OCR run is a fresh guess: any prior user confirmation is no
        // longer valid for the new (potentially different) terminal pick.
        UserExplicitlyConfirmedTerminal = false;
        _logger.LogInformation("Manual re-run OCR requested for {Path}", _bound.ImagePath);
        _ocr.Reprocess(_bound);
    }

    [RelayCommand(CanExecute = nameof(CanSubmit))]
    private async Task SubmitAsync()
    {
        var payload = BuildPayload();

        var validation = _validator.Validate(payload);
        if (validation.IsBlocking)
        {
            var msg = string.Join(Environment.NewLine,
                validation.Issues.Where(i => i.Severity == ValidationSeverity.Error)
                                 .Select(i => $"- {i.Message}"));
            _dialog.ShowError("Validation failed",
                "The payload contains blocking errors and cannot be submitted:\n\n" + msg);
            return;
        }

        DuplicateReport dup;
        try
        {
            dup = await _dupChecker.CheckAsync(payload);
        }
        catch (Exception ex)
        {
            _dialog.ShowError("Duplicate check failed", ex.Message);
            return;
        }

        // The dialog only DISPLAYS the production mode now (badge in the footer);
        // the toggle has moved to Settings to remove the per-screenshot footgun.
        // Pre-seed it from the live preference so the user sees the actual mode
        // their next click will commit in.
        var confirmVm = new ConfirmSubmitViewModel(payload, validation, dup,
            UexApiClient.SerialiseWirePayload(payload))
        {
            IsProduction = _prefs.DefaultIsProduction,
            Branch = Branch,
        };

        var confirmed = await _dialog.ShowConfirmSubmitAsync(confirmVm);
        if (!confirmed) return;

        payload.IsProduction = confirmVm.IsProduction ? 1 : 0;

        try
        {
            var result = await _api.SubmitDataAsync(payload);

            // Resolve the FULL list of source files this submission represents.
            // For a single-shot import that's [SourceImagePath]; for a merged
            // item it's all the merged file paths. We pass basenames to the
            // history (UEX only sees one anyway) so the watcher can match
            // against DirectoryInfo.EnumerateFiles().Name later.
            var allSourcePaths = (_bound?.SourcePaths is { Count: > 0 } sp)
                ? sp
                : new List<string> { SourceImagePath };
            var allSourceNames = allSourcePaths
                .Select(Path.GetFileName)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n!)
                .ToList();

            await _history.RecordAsync(new SubmissionRecord
            {
                IdTerminal = payload.IdTerminal,
                TerminalDisplayName = SelectedTerminal?.DisplayName,
                IsProduction = payload.IsProduction == 1,
                Ok = result.Ok,
                HttpStatusCode = result.HttpStatusCode,
                ApiStatus = result.Status,
                ApiMessage = result.Message,
                SourceImage = Path.GetFileName(SourceImagePath),
                SourceImages = allSourceNames,
                RequestJson = result.SerialisedRequestBody,
                ResponseJson = result.RawResponseBody,
                SubmittedCommodityIds = payload.Prices.Select(p => p.IdCommodity).ToList(),
            });

            // Try to delete EVERY source .png if the user opted in. Only on
            // PRODUCTION submissions and only if the API accepted them — never
            // on test or failed submissions (those should remain editable /
            // retryable). For merged items, this wipes all 2-3 source files in
            // one shot rather than leaving stragglers in the watched folder.
            var deletedCount = 0;
            if (result.Ok
                && payload.IsProduction == 1
                && _prefs.DeleteScreenshotAfterSubmit
                && allSourcePaths.Count > 0)
            {
                foreach (var p in allSourcePaths)
                {
                    if (!string.IsNullOrWhiteSpace(p) && TryDeleteSourceFile(p))
                        deletedCount++;
                }
            }

            if (_bound is not null)
            {
                _bound.Status = result.Ok ? InboxStatus.Sent : InboxStatus.Failed;
                _bound.StatusReason = result.Ok
                    ? (payload.IsProduction == 1
                        ? (deletedCount > 0
                            ? (allSourcePaths.Count > 1
                                ? $"Sent (production) · {deletedCount}/{allSourcePaths.Count} files deleted"
                                : "Sent (production) · file deleted")
                            : "Sent (production)")
                        : "Sent (test, is_production=0)")
                    : $"HTTP {result.HttpStatusCode} {result.Status}: {result.Message}";
            }

            if (result.Ok)
            {
                string dialogBody;
                if (payload.IsProduction == 1)
                {
                    if (deletedCount > 0)
                    {
                        dialogBody = allSourcePaths.Count > 1
                            ? $"UEX accepted the submission (PRODUCTION).\n\n{deletedCount} of {allSourcePaths.Count} source screenshots have been deleted from disk per your preference. The submission record is kept in History."
                            : "UEX accepted the submission (PRODUCTION).\n\nThe source screenshot file has been deleted from disk per your preference. The submission record is kept in History.";
                    }
                    else
                    {
                        dialogBody = "UEX accepted the submission (PRODUCTION).";
                    }
                }
                else
                {
                    dialogBody = "UEX accepted the submission (TEST mode, is_production=0). Nothing committed live.";
                }
                _dialog.ShowInfo("Submission accepted", dialogBody);
            }
            else
            {
                _dialog.ShowError("Submission rejected", BuildRejectionMessage(result));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "POST data_submit failed");
            _dialog.ShowError("Submission error", ex.Message);
        }
    }

    /// <summary>
    /// Best-effort delete of the source .png after a successful submission.
    /// Errors are logged but never bubbled — the user has already received the
    /// "Submission accepted" notification, and a transient I/O failure (file
    /// locked by AV scanner, etc.) should not poison the UX.
    /// </summary>
    private bool TryDeleteSourceFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                _logger.LogInformation("Post-submit delete: file already gone: {Path}", path);
                return false;
            }
            File.Delete(path);
            _logger.LogInformation("Post-submit delete: removed {Path}", path);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Post-submit delete failed for {Path}; file kept on disk.", path);
            return false;
        }
    }

    /// <summary>
    /// Decodes the (sometimes cryptic) UEX rejection codes into actionable
    /// French/English messages that explain what the user should fix.
    /// </summary>
    private static string BuildRejectionMessage(UexSubmitResult result)
    {
        var status = result.Status?.ToLowerInvariant() ?? "";

        var hint = status switch
        {
            "not_allowed" or "no_api_found" or "user_not_allowed" =>
                "UEX rejected this submission. Common causes (in order of likelihood):\n\n" +
                "1) MISSING APP TOKEN — UEX requires BOTH credentials on every submission:\n" +
                "   • your USER secret-key (Account page → Secret Key)\n" +
                "   • an APP bearer token (uexcorp.space/api/apps → create an app)\n" +
                "   Open Settings and make sure both are configured (green 'Saved on this PC' badges).\n\n" +
                "2) NEW DATARUNNER (90-day evaluation period) — UEX requires a screenshot " +
                "attached to every submission during this period. Open Settings and make sure " +
                "'Attach screenshot on submit' is ON.\n\n" +
                "3) WRONG VALUES — double-check the secret-key really comes from the Account " +
                "page (not a stray copy from /api/apps) and the bearer token really comes from " +
                "an app you own (not from another user).",

            "screenshot_required" =>
                "UEX requires a screenshot to be attached during your 90-day evaluation period. " +
                "Open Settings and enable 'Attach screenshot on submit', then re-send.",

            "missing_secret_key" =>
                "No secret-key was sent. Go to Settings and save your UEX secret-key first.",

            "invalid_secret_key" =>
                "The secret-key UEX received is not recognised. Double-check the value " +
                "on your UEX Account page (not /api/apps) and re-paste it in Settings.",

            "screenshot_length_exceeds_limit" =>
                "The attached screenshot is over 10 MB. Use a smaller image (PNG/JPG, lower resolution).",

            "image_upload_error" or "image_storage_error" =>
                "UEX failed to process the attached screenshot. Try a different file or PNG/JPG format.",

            "user_disabled" =>
                "Your UEX account is currently disabled or banned from data submissions. " +
                "Contact UEX staff on Discord.",

            "duplicated_report" =>
                "UEX already received an identical report for this terminal in the last 5 minutes. " +
                "Wait a bit before resending or change at least one value.",

            "ptu_reports_not_allowed" =>
                "UEX has temporarily disabled PTU reports — this typically happens during patch transitions when the PTU build is too unstable to feed public prices. " +
                "Your submission has been kept in the local History so you can resubmit it once UEX re-opens PTU. " +
                "If you submitted by mistake (this screenshot is actually from LIVE), open the editor's 'Optional metadata' panel and change the GAME VERSION to the LIVE build, then resend.",

            "invalid_game_version" =>
                "UEX rejected the GAME VERSION value. Allowed values are the current LIVE or PTU build numbers from " +
                "https://api.uexcorp.space/2.0/game_versions (e.g. \"4.7.2\" or \"4.8.0\"), or the literal strings \"LIVE\" / \"PTU\". " +
                "Open Settings → Screenshots folders → 'Refresh versions' to fetch the latest values, then resend.",

            "max_rows_exceeded" =>
                "Too many commodity rows in this submission (UEX limit is 500). Split into smaller batches.",

            "too_many_reports" =>
                "You hit the UEX rate limit (1000 reports per 30 min). Wait a bit before resending.",

            _ => null,
        };

        var lines = new List<string>
        {
            $"HTTP {result.HttpStatusCode}  ·  status = {result.Status ?? "(none)"}",
        };
        if (!string.IsNullOrWhiteSpace(result.Message))
            lines.Add(result.Message!);
        if (hint is not null)
        {
            lines.Add("");
            lines.Add(hint);
        }
        return string.Join("\n", lines);
    }

    private UexDataSubmitPayload BuildPayload()
    {
        // Hard guard against silent Unknown → Buy mirroring. The live
        // validator already raises a blocking error for this case, so
        // BuildPayload should never be reached with Tab=Unknown — but a
        // defence-in-depth check is cheap and prevents future regressions
        // from re-introducing the 2026-05-05 Pyro corruption pattern.
        if (Tab == TerminalTab.Unknown)
        {
            throw new InvalidOperationException(
                "Cannot build /data_submit payload while Tab is Unknown. " +
                "The user must pick BUY or SELL explicitly to avoid mirrored " +
                "price submissions on UEX.");
        }

        var isBuyTab = Tab == TerminalTab.Buy;
        var payload = new UexDataSubmitPayload
        {
            IdTerminal = SelectedTerminal?.Id ?? 0,
            Type = "commodity",
            IsProduction = IsProduction ? 1 : 0,
            ContainerSizes = string.IsNullOrWhiteSpace(ContainerSizes) ? null : ContainerSizes.Trim(),
            GameVersion = string.IsNullOrWhiteSpace(GameVersion) ? null : GameVersion.Trim(),
            Details = string.IsNullOrWhiteSpace(Details) ? null : Details.Trim(),
            Meta = new PayloadMeta
            {
                Draft = true,
                SourceImage = Path.GetFileName(SourceImagePath),
                TerminalDisplayName = SelectedTerminal?.DisplayName,
                TerminalMatchScore = TerminalMatchScore,
                TerminalMatchedField = TerminalSourceField,
                TerminalMatchedFromOcr = TerminalFromOcr,
                TabDetected = Tab.ToString().ToLowerInvariant(),
            },
        };

        foreach (var r in Rows)
        {
            if (r.Commodity is null) continue;
            var row = new UexPriceRow { IdCommodity = r.Commodity.Id };
            if (isBuyTab)
            {
                row.PriceBuy = r.PriceValue;
                row.ScuBuy = r.ScuValue;
                row.StatusBuy = r.Status == InventoryStatus.Unknown ? null : (int)r.Status;
            }
            else
            {
                row.PriceSell = r.PriceValue;
                row.ScuSell = r.ScuValue;
                row.StatusSell = r.Status == InventoryStatus.Unknown ? null : (int)r.Status;
            }
            payload.Prices.Add(row);
            payload.Meta.CommodityMatchScores.Add((int)Math.Round(r.MatchScore));
        }

        if (_prefs.AttachScreenshotOnSubmit)
        {
            payload.Screenshot = TryEncodeScreenshot(SourceImagePath);
        }

        return payload;
    }

    /// <summary>
    /// Reads the source screenshot from disk and base64-encodes it so it can
    /// ride along inside the JSON payload. Returns null if the file is missing,
    /// unreadable, or larger than UEX's 10 MB limit (in which case we silently
    /// skip rather than break the whole submission — the user-facing rejection
    /// hint will tell them to use a smaller image).
    /// </summary>
    private string? TryEncodeScreenshot(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            var info = new FileInfo(path);
            if (info.Length == 0 || info.Length > MaxScreenshotBytes)
            {
                _logger.LogWarning(
                    "Screenshot {Path} is {Size} bytes — outside the UEX 10 MB limit, skipping attach.",
                    path, info.Length);
                return null;
            }
            var bytes = File.ReadAllBytes(path);
            return System.Convert.ToBase64String(bytes);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to base64-encode screenshot {Path}", path);
            return null;
        }
    }
}

public sealed partial class EditableRow : ObservableObject
{
    [ObservableProperty] private UexCommodity? _commodity;
    [ObservableProperty] private int? _scuValue;
    [ObservableProperty] private double? _priceValue;
    [ObservableProperty] private InventoryStatus _status = InventoryStatus.Unknown;
    [ObservableProperty] private double _matchScore;
    [ObservableProperty] private string _fromOcr = "";

    /// <summary>True if SCU is missing or invalid. SCU == 0 is valid ONLY when
    /// the status is OutOfStock (by definition, out of stock = 0 SCU). For all
    /// other statuses, SCU must be a positive number.</summary>
    public bool ScuIsEmpty => ScuValue is null
        || (ScuValue <= 0 && Status != InventoryStatus.OutOfStock);

    /// <summary>True if Price is missing/zero. Empty price = blocking error.</summary>
    public bool PriceIsEmpty => PriceValue is null or <= 0;

    /// <summary>True if no commodity has been picked yet.</summary>
    public bool CommodityIsMissing => Commodity is null;

    /// <summary>True when the inventory status was not detected by OCR. Drives
    /// the orange tint on the Status combobox so the user notices they need
    /// to pick one.</summary>
    public bool StatusIsUnknown => Status == InventoryStatus.Unknown;

    /// <summary>
    /// Severity for the match badge, kept in sync with the validation thresholds
    /// in <see cref="ScreenshotEditViewModel.RecomputeValidation"/>:
    ///   100   → "Ok"       (green)
    ///   85-99 → "Warning"  (orange, non-blocking)
    ///   &lt; 85  → "Error"    (red, blocking unless the user explicitly ticks
    ///                       "I have reviewed everything" in the footer).
    /// </summary>
    public string MatchSeverity => MatchScore switch
    {
        >= 100 => "Ok",
        >= 85 => "Warning",
        _ => "Error",
    };

    // Force re-evaluation of the dependent computed properties whenever
    // one of the backing fields changes. Without this, the cell coloring
    // in the DataGrid would not refresh after the user edits a value.
    partial void OnCommodityChanged(UexCommodity? value) => OnPropertyChanged(nameof(CommodityIsMissing));
    partial void OnScuValueChanged(int? value) => OnPropertyChanged(nameof(ScuIsEmpty));
    partial void OnPriceValueChanged(double? value) => OnPropertyChanged(nameof(PriceIsEmpty));
    partial void OnStatusChanged(InventoryStatus value)
    {
        OnPropertyChanged(nameof(StatusIsUnknown));
        OnPropertyChanged(nameof(ScuIsEmpty));
    }
    partial void OnMatchScoreChanged(double value) => OnPropertyChanged(nameof(MatchSeverity));
}
