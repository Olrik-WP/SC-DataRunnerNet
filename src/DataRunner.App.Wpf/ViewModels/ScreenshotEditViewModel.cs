using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DataRunner.App.Services;
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
    }

    public void Load(InboxItem item)
    {
        _bound = item;
        CanReRunOcr = true;
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

    partial void OnSelectedTerminalChanged(UexTerminal? value)
    {
        if (_suspendSearchSync || value is null) return;
        _suspendSearchSync = true;
        TerminalSearch = value.DisplayName;
        _suspendSearchSync = false;
        IsTerminalDropDownOpen = false;
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
        foreach (var t in source.OrderBy(t => t.DisplayName).Take(50))
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
        _logger.LogInformation("Manual re-run OCR requested for {Path}", _bound.ImagePath);
        _ocr.Reprocess(_bound);
    }

    [RelayCommand]
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

        var confirmVm = new ConfirmSubmitViewModel(payload, validation, dup,
            UexApiClient.SerialiseWirePayload(payload));

        var confirmed = await _dialog.ShowConfirmSubmitAsync(confirmVm);
        if (!confirmed) return;

        payload.IsProduction = confirmVm.IsProduction ? 1 : 0;

        try
        {
            var result = await _api.SubmitDataAsync(payload);
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
                RequestJson = result.SerialisedRequestBody,
                ResponseJson = result.RawResponseBody,
                SubmittedCommodityIds = payload.Prices.Select(p => p.IdCommodity).ToList(),
            });

            // Try to delete the source .png if the user opted in. Only on
            // PRODUCTION submissions and only if the API accepted them — never
            // on test or failed submissions (those should remain editable / retryable).
            var deleted = false;
            if (result.Ok
                && payload.IsProduction == 1
                && _prefs.DeleteScreenshotAfterSubmit
                && !string.IsNullOrWhiteSpace(SourceImagePath))
            {
                deleted = TryDeleteSourceFile(SourceImagePath);
            }

            if (_bound is not null)
            {
                _bound.Status = result.Ok ? InboxStatus.Sent : InboxStatus.Failed;
                _bound.StatusReason = result.Ok
                    ? (payload.IsProduction == 1
                        ? (deleted ? "Sent (production) · file deleted" : "Sent (production)")
                        : "Sent (test, is_production=0)")
                    : $"HTTP {result.HttpStatusCode} {result.Status}: {result.Message}";
            }

            if (result.Ok)
            {
                var dialogBody = payload.IsProduction == 1
                    ? (deleted
                        ? "UEX accepted the submission (PRODUCTION).\n\nThe source screenshot file has been deleted from disk per your preference. The submission record is kept in History."
                        : "UEX accepted the submission (PRODUCTION).")
                    : "UEX accepted the submission (TEST mode, is_production=0). Nothing committed live.";
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
}
