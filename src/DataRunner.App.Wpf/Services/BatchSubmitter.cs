using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using DataRunner.App.ViewModels;
using DataRunner.Core.Abstractions;
using DataRunner.Core.Models;
using DataRunner.UexClient;
using Microsoft.Extensions.Logging;

namespace DataRunner.App.Services;

/// <summary>
/// Drives the sequential POST loop of a <see cref="BatchPlan"/>:
/// each <see cref="PlannedSubmission"/> is converted to a wire payload, run
/// through the existing payload validator, and POSTed via
/// <see cref="IUexApiClient.SubmitDataAsync"/> with throttling and a small
/// exponential backoff for transient UEX rate-limit errors.
///
/// Exposes the per-item lifecycle to the UI as an <see cref="IAsyncEnumerable{T}"/>
/// of <see cref="BatchProgress"/> events. The caller (typically <see cref="InboxViewModel"/>)
/// updates the matching <see cref="InboxItem.Status"/> on each event so the
/// inbox cards mirror the live progress without coupling this service to the
/// UI dispatcher.
/// </summary>
public interface IBatchSubmitter
{
    IAsyncEnumerable<BatchProgress> RunAsync(BatchPlan plan, BatchOptions options, CancellationToken ct = default);
}

public sealed class BatchSubmitter : IBatchSubmitter
{
    private readonly IUexApiClient _api;
    private readonly IPayloadValidator _validator;
    private readonly IDuplicateChecker _dupChecker;
    private readonly ISubmissionHistory _history;
    private readonly IAppPreferences _prefs;
    private readonly ICatalogProvider _catalog;
    private readonly ILogger<BatchSubmitter> _logger;

    /// <summary>UEX hard limit for the `screenshot` field (10 MB raw → ~13.4 MB base64).</summary>
    private const long MaxScreenshotBytes = 10L * 1024 * 1024;

    /// <summary>Default backoff schedule for transient rate-limit errors.</summary>
    private static readonly TimeSpan[] BackoffSteps =
    {
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(8),
        TimeSpan.FromSeconds(16),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(60),
    };

    private const int MaxAutoRetries = 3;

    public BatchSubmitter(
        IUexApiClient api,
        IPayloadValidator validator,
        IDuplicateChecker dupChecker,
        ISubmissionHistory history,
        IAppPreferences prefs,
        ICatalogProvider catalog,
        ILogger<BatchSubmitter> logger)
    {
        _api = api;
        _validator = validator;
        _dupChecker = dupChecker;
        _history = history;
        _prefs = prefs;
        _catalog = catalog;
        _logger = logger;
    }

    public async IAsyncEnumerable<BatchProgress> RunAsync(
        BatchPlan plan,
        BatchOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var throttle = TimeSpan.FromMilliseconds(Math.Max(0, _prefs.BatchSubmissionDelayMs));

        for (var i = 0; i < plan.Submissions.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var planned = plan.Submissions[i];

            // Pre-flight: nothing to send for this screenshot (eg. all of its
            // commodities were claimed by newer captures during dedup). Skip
            // it but report the no-op so the UI can mark the card as "Sent
            // (nothing to send)" without keeping it in the Validated state.
            if (planned.Rows.Count == 0)
            {
                yield return BatchProgress.Skipped(
                    planned,
                    "All commodities for this screenshot were superseded by more recent captures in this batch.");
                if (i < plan.Submissions.Count - 1)
                    await DelayAsync(throttle, ct).ConfigureAwait(false);
                continue;
            }

            yield return BatchProgress.Sending(planned);

            // Each item is retried up to MaxAutoRetries times for transient
            // UEX rate-limit errors. We keep the per-item retry inside the
            // big loop so the throttle between two DIFFERENT items fires
            // even when the previous one needed retries.
            BatchItemOutcome outcome;
            try
            {
                outcome = await SendOneAsync(planned, options, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error during batch send for item #{Pos}", planned.QueuePosition);
                outcome = BatchItemOutcome.Failure(
                    httpStatusCode: 0,
                    apiStatus: null,
                    message: ex.Message,
                    requestJson: "",
                    responseJson: "",
                    deletedFiles: 0);
            }

            yield return BatchProgress.Done(planned, outcome);

            if (i < plan.Submissions.Count - 1)
                await DelayAsync(throttle, ct).ConfigureAwait(false);
        }
    }

    private async Task<BatchItemOutcome> SendOneAsync(
        PlannedSubmission planned,
        BatchOptions options,
        CancellationToken ct)
    {
        // Build the wire payload from the planned (post-dedup) row list. We
        // rebuild it on every retry attempt so a backoff loop never re-uses
        // a stale base64 image read (paranoid against on-disk file changes
        // between retries — extremely unlikely but free to defend against).
        var payload = BuildPayload(planned, options);

        var validation = _validator.Validate(payload);
        if (validation.IsBlocking)
        {
            // Blocking validation errors should never reach here because the
            // editor's live validation already gates Validate. Defence in
            // depth: if we slipped through, surface the issues clearly so
            // the user knows what to fix and the item lands in Failed.
            var msg = string.Join(Environment.NewLine,
                validation.Issues
                    .Where(i => i.Severity == ValidationSeverity.Error)
                    .Select(i => $"- {i.Message}"));
            return BatchItemOutcome.Failure(
                httpStatusCode: 0,
                apiStatus: "validation_blocked",
                message: $"Validator refused the payload before sending:\n{msg}",
                requestJson: UexApiClient.SerialiseWirePayload(payload),
                responseJson: "",
                deletedFiles: 0);
        }

        // Optional duplicate guard. The smart-split planner should have
        // already eliminated cross-screenshot duplicates inside this batch,
        // so the most likely case here is a duplicate against a PRIOR batch
        // (same user resubmitting < 5 min later) or against another user's
        // very recent submission. We honour the dup-checker's BLOCK verdict
        // unless the user explicitly opted out via BatchOptions.SkipDuplicateCheck.
        if (!options.SkipDuplicateCheck)
        {
            DuplicateReport dup;
            try
            {
                dup = await _dupChecker.CheckAsync(payload, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Duplicate-check failed for batch item #{Pos}; sending blind", planned.QueuePosition);
                dup = new DuplicateReport(DuplicateSeverity.Ok, Array.Empty<DuplicateFinding>(), Array.Empty<UexCommodityRawPrice>());
            }

            if (dup.Worst == DuplicateSeverity.Block)
            {
                var details = string.Join(Environment.NewLine,
                    dup.Findings
                        .Where(f => f.Severity == DuplicateSeverity.Block)
                        .Select(f => $"- {f.CommodityLabel}: {f.Reason}"));
                return BatchItemOutcome.Failure(
                    httpStatusCode: 0,
                    apiStatus: "duplicated_report",
                    message: $"Local duplicate guard refused the submission (UEX would reject):\n{details}",
                    requestJson: UexApiClient.SerialiseWirePayload(payload),
                    responseJson: "",
                    deletedFiles: 0);
            }
        }

        // POST loop with bounded retries on transient rate-limit errors.
        UexSubmitResult? result = null;
        for (var attempt = 0; attempt <= MaxAutoRetries; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                result = await _api.SubmitDataAsync(payload, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "POST data_submit failed for batch item #{Pos}", planned.QueuePosition);
                return BatchItemOutcome.Failure(
                    httpStatusCode: 0,
                    apiStatus: "network_error",
                    message: ex.Message,
                    requestJson: UexApiClient.SerialiseWirePayload(payload),
                    responseJson: "",
                    deletedFiles: 0);
            }

            if (result.Ok || !ShouldRetry(result, attempt)) break;

            var delay = BackoffSteps[Math.Min(attempt, BackoffSteps.Length - 1)];
            _logger.LogWarning(
                "UEX rate limited (status={Status}, http={Code}) — waiting {Delay}s before retry {Attempt}/{Max}",
                result.Status, result.HttpStatusCode, delay.TotalSeconds, attempt + 1, MaxAutoRetries);
            await DelayAsync(delay, ct).ConfigureAwait(false);
        }

        var finalResult = result!;
        var allSourcePaths = (planned.SourceItem.SourcePaths is { Count: > 0 } sp)
            ? sp
            : new List<string> { planned.SourceItem.ImagePath };
        var allSourceNames = allSourcePaths
            .Select(Path.GetFileName)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n!)
            .ToList();

        await _history.RecordAsync(new SubmissionRecord
        {
            IdTerminal = payload.IdTerminal,
            TerminalDisplayName = planned.TerminalLabel,
            IsProduction = payload.IsProduction == 1,
            Ok = finalResult.Ok,
            HttpStatusCode = finalResult.HttpStatusCode,
            ApiStatus = finalResult.Status,
            ApiMessage = finalResult.Message,
            SourceImage = Path.GetFileName(planned.SourceItem.ImagePath),
            SourceImages = allSourceNames,
            RequestJson = finalResult.SerialisedRequestBody,
            ResponseJson = finalResult.RawResponseBody,
            SubmittedCommodityIds = payload.Prices.Select(p => p.IdCommodity).ToList(),
        }, ct).ConfigureAwait(false);

        // Best-effort cleanup: if the user opted in AND the submission was
        // accepted in PRODUCTION, delete every source screenshot from disk
        // so the watched folder doesn't keep replaying them on rescan.
        var deletedCount = 0;
        if (finalResult.Ok
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

        return finalResult.Ok
            ? BatchItemOutcome.Success(finalResult.HttpStatusCode, finalResult.Status, finalResult.Message,
                                       finalResult.SerialisedRequestBody, finalResult.RawResponseBody, deletedCount)
            : BatchItemOutcome.Failure(finalResult.HttpStatusCode, finalResult.Status, finalResult.Message,
                                       finalResult.SerialisedRequestBody, finalResult.RawResponseBody, deletedCount);
    }

    /// <summary>
    /// Should we retry on this error? YES for transient rate-limit / server
    /// hiccups (HTTP 429, <c>too_many_reports</c>, HTTP 5xx). NO for
    /// <c>duplicated_report</c> (definitive within 5 min, no point retrying)
    /// and 4xx auth / validation errors (re-submitting the same payload won't
    /// fix the problem).
    /// </summary>
    private static bool ShouldRetry(UexSubmitResult r, int attempt)
    {
        if (attempt >= MaxAutoRetries) return false;
        var status = (r.Status ?? "").Trim().ToLowerInvariant();
        if (status == "too_many_reports") return true;
        if (r.HttpStatusCode == 429) return true;
        if (r.HttpStatusCode >= 500 && r.HttpStatusCode < 600) return true;
        return false;
    }

    private static async Task DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        if (delay <= TimeSpan.Zero) return;
        try { await Task.Delay(delay, ct).ConfigureAwait(false); }
        catch (TaskCanceledException) { throw new OperationCanceledException(ct); }
    }

    private UexDataSubmitPayload BuildPayload(PlannedSubmission planned, BatchOptions options)
    {
        var item = planned.SourceItem;
        var submission = item.Submission ?? throw new InvalidOperationException(
            "PlannedSubmission.SourceItem.Submission is null — should have been gated upstream.");

        if (submission.Tab == TerminalTab.Unknown)
        {
            // Same hard guard as the editor's BuildPayload — defence in depth.
            // The validator above will already have caught this, but raising
            // here too keeps the failure mode tight if validation is ever
            // bypassed (e.g. SkipDuplicateCheck=true, custom unit tests).
            throw new InvalidOperationException(
                $"Cannot build /data_submit payload for screenshot #{planned.QueuePosition}: " +
                "Tab is Unknown — the user must pick BUY or SELL explicitly.");
        }

        var isBuyTab = submission.Tab == TerminalTab.Buy;
        var isProduction = item.DraftIsProduction ?? options.DefaultIsProduction;

        var payload = new UexDataSubmitPayload
        {
            IdTerminal = submission.IdTerminal ?? 0,
            Type = "commodity",
            IsProduction = isProduction ? 1 : 0,
            ContainerSizes = string.IsNullOrWhiteSpace(submission.ContainerSizes) ? null : submission.ContainerSizes.Trim(),
            GameVersion = ResolveGameVersion(item, options),
            Details = string.IsNullOrWhiteSpace(item.DraftDetails) ? null : item.DraftDetails!.Trim(),
            Meta = new PayloadMeta
            {
                Draft = false,
                SourceImage = Path.GetFileName(item.ImagePath),
                TerminalDisplayName = submission.TerminalDisplayName,
                TerminalMatchScore = submission.TerminalMatchScore,
                TerminalMatchedField = submission.TerminalMatchedField ?? "",
                TerminalMatchedFromOcr = submission.TerminalMatchedFromOcr ?? "",
                TabDetected = submission.Tab.ToString().ToLowerInvariant(),
            },
        };

        foreach (var r in planned.Rows)
        {
            if (r.IdCommodity is not int cid) continue;
            var row = new UexPriceRow { IdCommodity = cid };
            if (isBuyTab)
            {
                row.PriceBuy = r.PriceBuy;
                row.ScuBuy = r.ScuBuy;
                row.StatusBuy = r.StatusBuy == InventoryStatus.Unknown ? null : (int)r.StatusBuy;
            }
            else
            {
                row.PriceSell = r.PriceBuy;
                row.ScuSell = r.ScuBuy;
                row.StatusSell = r.StatusBuy == InventoryStatus.Unknown ? null : (int)r.StatusBuy;
            }
            payload.Prices.Add(row);
            payload.Meta.CommodityMatchScores.Add((int)Math.Round(r.CommodityMatchScore));
        }

        if (_prefs.AttachScreenshotOnSubmit)
        {
            payload.Screenshot = TryEncodeScreenshot(item.ImagePath);
        }

        return payload;
    }

    /// <summary>
    /// Resolves the <c>game_version</c> wire field for an item. User overrides
    /// (typed in the editor's "Optional metadata" panel) win; otherwise we
    /// fall back to the branch-resolved value provided by <see cref="BatchOptions"/>
    /// (cached at batch-start time so a single batch is internally consistent
    /// even if /game_versions refreshes mid-run).
    /// </summary>
    private static string? ResolveGameVersion(InboxItem item, BatchOptions options)
    {
        if (!string.IsNullOrWhiteSpace(item.DraftGameVersion))
            return item.DraftGameVersion!.Trim();

        return item.Branch == GameBranch.Ptu
            ? (string.IsNullOrWhiteSpace(options.PtuGameVersion) ? null : options.PtuGameVersion)
            : (string.IsNullOrWhiteSpace(options.LiveGameVersion) ? null : options.LiveGameVersion);
    }

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
            return Convert.ToBase64String(bytes);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to base64-encode screenshot {Path}", path);
            return null;
        }
    }

    private bool TryDeleteSourceFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Post-submit delete failed for {Path}; file kept on disk.", path);
            return false;
        }
    }
}

/// <summary>
/// Per-item event surfaced by <see cref="IBatchSubmitter.RunAsync"/>. The UI
/// pattern-matches on <see cref="Phase"/> to update the inbox card.
/// </summary>
public sealed record BatchProgress(
    PlannedSubmission Item,
    BatchProgressPhase Phase,
    BatchItemOutcome? Outcome,
    string? SkipReason)
{
    public static BatchProgress Sending(PlannedSubmission item) =>
        new(item, BatchProgressPhase.Sending, null, null);

    public static BatchProgress Done(PlannedSubmission item, BatchItemOutcome outcome) =>
        new(item, BatchProgressPhase.Done, outcome, null);

    public static BatchProgress Skipped(PlannedSubmission item, string reason) =>
        new(item, BatchProgressPhase.Skipped, null, reason);
}

public enum BatchProgressPhase { Sending, Done, Skipped }

/// <summary>
/// Final result of a single submission inside a batch. Mirrors the relevant
/// bits of <see cref="UexSubmitResult"/> plus the deleted-file count for the
/// status reason text on the inbox card.
/// </summary>
public sealed record BatchItemOutcome(
    bool Ok,
    int HttpStatusCode,
    string? ApiStatus,
    string? Message,
    string RequestJson,
    string ResponseJson,
    int DeletedFiles)
{
    public static BatchItemOutcome Success(int httpStatusCode, string? apiStatus, string? message,
                                           string requestJson, string responseJson, int deletedFiles) =>
        new(true, httpStatusCode, apiStatus, message, requestJson, responseJson, deletedFiles);

    public static BatchItemOutcome Failure(int httpStatusCode, string? apiStatus, string? message,
                                           string requestJson, string responseJson, int deletedFiles) =>
        new(false, httpStatusCode, apiStatus, message, requestJson, responseJson, deletedFiles);
}

/// <summary>
/// Caller-controlled knobs passed to <see cref="IBatchSubmitter.RunAsync"/>.
/// We snapshot them at batch start so the user can safely flip Settings
/// while the batch is running without skewing the in-flight items.
/// </summary>
public sealed record BatchOptions(
    bool DefaultIsProduction,
    string? LiveGameVersion,
    string? PtuGameVersion,
    bool SkipDuplicateCheck = false);
