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
    private readonly OcrCoordinator _ocr;
    private readonly ILogger<ScreenshotEditViewModel> _logger;

    /// <summary>UEX hard limit for the `screenshot` field (10 MB raw → ~13.4 MB base64).</summary>
    private const long MaxScreenshotBytes = 10L * 1024 * 1024;

    private InboxItem? _bound;
    private bool _suspendSearchSync;

    [ObservableProperty] private BitmapImage? _previewImage;
    [ObservableProperty] private string _sourceImagePath = "";
    [ObservableProperty] private bool _isProduction;

    [ObservableProperty] private UexTerminal? _selectedTerminal;
    [ObservableProperty] private string _terminalSearch = "";
    [ObservableProperty] private bool _isTerminalDropDownOpen;
    [ObservableProperty] private TerminalTab _tab = TerminalTab.Buy;
    [ObservableProperty] private string _containerSizes = "";
    [ObservableProperty] private string _gameVersion = "";
    [ObservableProperty] private string _details = "";

    [ObservableProperty] private double _terminalMatchScore;
    [ObservableProperty] private string _terminalSourceField = "";
    [ObservableProperty] private string _terminalFromOcr = "";

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
        _ocr = ocr;
        _logger = logger;

        foreach (var c in _catalog.Commodities.OrderBy(c => c.Name))
            CommodityOptions.Add(c);

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
        TerminalSearch = value.DisplayName;
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

    /// <summary>
    /// Walks the current state and rebuilds the issue list. Cheap (sync, no I/O).
    /// Single source of truth for the Send-button gate AND the footer panel.
    /// </summary>
    private void RecomputeValidation()
    {
        ValidationIssues.Clear();

        if (SelectedTerminal is null)
        {
            ValidationIssues.Add(new LiveValidationIssue
            {
                Severity = LiveValidationSeverity.Error,
                Code = "terminal_missing",
                Message = "No terminal picked. Select one in the TERMINAL field.",
            });
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
                    Message = $"\"{SelectedTerminal.DisplayName}\" exists in multiple star systems — pick the correct one in the dropdown to confirm.",
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

        if (Rows.Count == 0)
        {
            ValidationIssues.Add(new LiveValidationIssue
            {
                Severity = LiveValidationSeverity.Error,
                Code = "no_rows",
                Message = "No commodity rows. Add at least one row before submitting.",
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

            if (r.Status == InventoryStatus.Unknown)
            {
                ValidationIssues.Add(new LiveValidationIssue
                {
                    Severity = LiveValidationSeverity.Warning,
                    Code = "row_status_unknown",
                    Message = $"{label}: inventory status is Unknown — pick one.",
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

    public void Load(InboxItem item)
    {
        _bound = item;
        CanReRunOcr = true;
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
        }

        UpdateTerminalSuggestions();
    }

    private void HydrateFrom(ParsedSubmission s)
    {
        Tab = s.Tab == TerminalTab.Unknown ? TerminalTab.Buy : s.Tab;
        ContainerSizes = s.ContainerSizes ?? "";
        TerminalMatchScore = s.TerminalMatchScore;
        TerminalSourceField = s.TerminalMatchedField ?? "";
        TerminalFromOcr = s.TerminalMatchedFromOcr ?? "";

        var resolved = s.IdTerminal is { } id ? _catalog.GetTerminal(id) : null;
        _suspendSearchSync = true;
        SelectedTerminal = resolved;
        TerminalSearch = resolved?.DisplayName ?? "";
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

        // Clear the resolved terminal if the user fully edited the text
        // (otherwise BuildPayload would silently send the previously-matched id).
        if (SelectedTerminal is not null
            && !string.Equals(SelectedTerminal.DisplayName, value, StringComparison.OrdinalIgnoreCase))
        {
            _suspendSearchSync = true;
            SelectedTerminal = null;
            _suspendSearchSync = false;
        }
    }

    private void UpdateTerminalSuggestions()
    {
        TerminalSuggestions.Clear();

        IEnumerable<UexTerminal> source = _catalog.CommodityTerminals;
        if (!string.IsNullOrWhiteSpace(TerminalSearch))
        {
            var q = TerminalSearch.Trim();
            source = source.Where(t =>
                Contains(t.DisplayName, q) ||
                Contains(t.Name, q) ||
                Contains(t.Nickname, q) ||
                Contains(t.Code, q) ||
                Contains(t.SpaceStationName, q) ||
                Contains(t.OutpostName, q) ||
                Contains(t.CityName, q));
        }

        // When the currently-selected terminal is ambiguous, ALWAYS surface every
        // candidate sharing its name first — even if those don't match the search
        // string — so the user can disambiguate in one click without having to
        // scroll or re-type. This is the UEX community fix for the Gateway issue.
        var pinned = new List<UexTerminal>();
        var pinnedIds = new HashSet<int>();
        if (IsAmbiguousTerminal && AmbiguousCandidates.Count > 0)
        {
            foreach (var c in AmbiguousCandidates.OrderBy(t => t.StarSystemName))
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

    private static bool Contains(string? s, string q)
        => s is not null && s.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;

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
                "PTU reports are currently disabled by the server. Make sure 'Game Version' is a LIVE version.",

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
        var isBuyTab = Tab is TerminalTab.Buy or TerminalTab.Unknown;
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

    /// <summary>True if SCU is missing/zero. Empty SCU + missing IsMissing flag = blocking error.</summary>
    public bool ScuIsEmpty => ScuValue is null or <= 0;

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
    partial void OnStatusChanged(InventoryStatus value) => OnPropertyChanged(nameof(StatusIsUnknown));
    partial void OnMatchScoreChanged(double value) => OnPropertyChanged(nameof(MatchSeverity));
}
