using DataRunner.Core.Abstractions;
using DataRunner.Core.Models;
using Microsoft.Extensions.Logging;

namespace DataRunner.UexClient;

/// <summary>
/// Two-stage duplicate / sanity check:
///
/// 1) LOCAL  : query <see cref="ISubmissionHistory"/> for any submission for the same
///             (id_terminal, id_commodity) within the last 5 minutes. UEX rejects these
///             server-side, so we mirror the rule locally to save round-trips and to give
///             the user an immediate, explicit reason.
///
/// 2) REMOTE : pull GET /commodities_raw_prices?id_terminal=X (one call) and for every row
///             we are about to submit, compare:
///               * |our_value - last_known| / last_known
///                 - &lt; 1%  AND date_modified &lt; 5min  -> BLOCK (likely re-submit)
///                 - &lt; 5%                              -> Info  (drift)
///                 - 5%..30%                              -> Warning
///                 - &gt; 30%                              -> Block (sanity ceiling)
/// </summary>
public sealed class DuplicateChecker : IDuplicateChecker
{
    private static readonly TimeSpan FiveMinutes = TimeSpan.FromMinutes(5);
    private const double IdentityThreshold = 0.01;
    private const double DriftWarningThreshold = 0.05;
    private const double DriftBlockThreshold = 0.30;

    private readonly IUexApiClient _api;
    private readonly ISubmissionHistory _history;
    private readonly ICatalogProvider _catalog;
    private readonly ILogger<DuplicateChecker> _logger;

    public DuplicateChecker(
        IUexApiClient api,
        ISubmissionHistory history,
        ICatalogProvider catalog,
        ILogger<DuplicateChecker> logger)
    {
        _api = api;
        _history = history;
        _catalog = catalog;
        _logger = logger;
    }

    public async Task<DuplicateReport> CheckAsync(UexDataSubmitPayload payload, CancellationToken ct = default)
    {
        var findings = new List<DuplicateFinding>();
        IReadOnlyList<UexCommodityRawPrice> live = Array.Empty<UexCommodityRawPrice>();

        if (payload.IdTerminal <= 0 || payload.Prices.Count == 0)
        {
            return new DuplicateReport(DuplicateSeverity.Ok, findings, live);
        }

        // ---- 1) LOCAL: 5-minute server-side duplicate guard ----
        // IMPORTANT: only successful submissions count. Rejected attempts (403,
        // network error, validation failure, etc.) never reached UEX's storage,
        // so re-sending them is NOT a duplicate from UEX's point of view.
        // Without this filter, a single 403 would lock the user out of retrying
        // the same data for 5 minutes.
        var recent = await _history.GetRecentByTerminalAsync(payload.IdTerminal, FiveMinutes, ct).ConfigureAwait(false);
        var recentlySubmittedIds = recent
            .Where(r => r.Ok)
            .SelectMany(r => r.SubmittedCommodityIds)
            .ToHashSet();

        for (var i = 0; i < payload.Prices.Count; i++)
        {
            var p = payload.Prices[i];
            if (recentlySubmittedIds.Contains(p.IdCommodity))
            {
                findings.Add(new DuplicateFinding(
                    RowIndex: i,
                    IdCommodity: p.IdCommodity,
                    CommodityLabel: LabelFor(p.IdCommodity),
                    Severity: DuplicateSeverity.Block,
                    Reason: "Same commodity submitted to this terminal in the last 5 minutes (UEX would reject)."));
            }
        }

        // ---- 2) REMOTE: fetch latest live snapshot ----
        try
        {
            live = await _api.GetCommodityRawPricesAsync(payload.IdTerminal, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Live price snapshot fetch failed; submitting blind.");
            findings.Add(new DuplicateFinding(
                RowIndex: -1,
                IdCommodity: 0,
                CommodityLabel: "(live snapshot)",
                Severity: DuplicateSeverity.Warning,
                Reason: $"Could not fetch GET /commodities_raw_prices: {ex.Message}"));
            return new DuplicateReport(WorstOf(findings), findings, live);
        }

        // Index live by id_commodity for O(1) lookups.
        var liveByCommodity = live
            .GroupBy(l => l.IdCommodity)
            .ToDictionary(g => g.Key, g => g.First());

        for (var i = 0; i < payload.Prices.Count; i++)
        {
            var p = payload.Prices[i];
            if (!liveByCommodity.TryGetValue(p.IdCommodity, out var liveRow))
            {
                findings.Add(new DuplicateFinding(
                    RowIndex: i,
                    IdCommodity: p.IdCommodity,
                    CommodityLabel: LabelFor(p.IdCommodity),
                    Severity: DuplicateSeverity.Info,
                    Reason: "No prior live data for this commodity at this terminal."));
                continue;
            }

            DateTimeOffset? remoteAt = liveRow.DateModified is { } ts
                ? DateTimeOffset.FromUnixTimeSeconds(ts)
                : null;

            // Buy side
            if (p.PriceBuy is double localBuy && liveRow.PriceBuy is double remoteBuy && remoteBuy > 0)
            {
                AppraiseDrift(i, p.IdCommodity, "price_buy", localBuy, remoteBuy, remoteAt, findings);
            }
            else if (p.PriceBuy is double localBuyOnly && (liveRow.PriceBuy is null || liveRow.PriceBuy == 0))
            {
                findings.Add(new DuplicateFinding(i, p.IdCommodity, LabelFor(p.IdCommodity),
                    DuplicateSeverity.Info, "First-known buy price for this terminal/commodity.",
                    LocalValue: localBuyOnly, RemoteValue: liveRow.PriceBuy, RemoteLastUpdate: remoteAt));
            }

            // Sell side
            if (p.PriceSell is double localSell && liveRow.PriceSell is double remoteSell && remoteSell > 0)
            {
                AppraiseDrift(i, p.IdCommodity, "price_sell", localSell, remoteSell, remoteAt, findings);
            }
            else if (p.PriceSell is double localSellOnly && (liveRow.PriceSell is null || liveRow.PriceSell == 0))
            {
                findings.Add(new DuplicateFinding(i, p.IdCommodity, LabelFor(p.IdCommodity),
                    DuplicateSeverity.Info, "First-known sell price for this terminal/commodity.",
                    LocalValue: localSellOnly, RemoteValue: liveRow.PriceSell, RemoteLastUpdate: remoteAt));
            }
        }

        return new DuplicateReport(WorstOf(findings), findings, live);
    }

    private void AppraiseDrift(
        int row,
        int idCommodity,
        string field,
        double local,
        double remote,
        DateTimeOffset? remoteAt,
        List<DuplicateFinding> findings)
    {
        var diffPct = Math.Abs(local - remote) / remote;

        DuplicateSeverity severity;
        string reason;

        var isFresh = remoteAt is { } at && (DateTimeOffset.UtcNow - at) < FiveMinutes;

        if (diffPct < IdentityThreshold && isFresh)
        {
            severity = DuplicateSeverity.Block;
            reason = $"{field}: virtually identical (<1% diff) to a value submitted in the last 5 min by someone else. UEX would reject.";
        }
        else if (diffPct > DriftBlockThreshold)
        {
            severity = DuplicateSeverity.Block;
            reason = $"{field}: drift {diffPct:P1} vs last live ({remote:F2} aUEC). Above {DriftBlockThreshold:P0} guard, please re-check.";
        }
        else if (diffPct > DriftWarningThreshold)
        {
            severity = DuplicateSeverity.Warning;
            reason = $"{field}: drift {diffPct:P1} vs last live ({remote:F2} aUEC). Confirm before submit.";
        }
        else if (diffPct < IdentityThreshold)
        {
            severity = DuplicateSeverity.Info;
            reason = $"{field}: matches last live within 1% (no recent submission, ok to confirm).";
        }
        else
        {
            severity = DuplicateSeverity.Info;
            reason = $"{field}: drift {diffPct:P1} vs last live ({remote:F2} aUEC). Within tolerance.";
        }

        findings.Add(new DuplicateFinding(
            RowIndex: row,
            IdCommodity: idCommodity,
            CommodityLabel: LabelFor(idCommodity),
            Severity: severity,
            Reason: reason,
            LocalValue: local,
            RemoteValue: remote,
            PercentDifference: diffPct,
            RemoteLastUpdate: remoteAt));
    }

    private string LabelFor(int idCommodity)
    {
        var c = _catalog.GetCommodity(idCommodity);
        return c is null ? $"#{idCommodity}" : $"{c.Name} ({c.Code})";
    }

    private static DuplicateSeverity WorstOf(IEnumerable<DuplicateFinding> f)
    {
        var s = DuplicateSeverity.Ok;
        foreach (var i in f) if (i.Severity > s) s = i.Severity;
        return s;
    }
}
