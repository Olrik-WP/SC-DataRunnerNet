using System.Text.Json;
using DataRunner.Core.Abstractions;
using DataRunner.Core.Models;
using Microsoft.Extensions.Logging;

namespace DataRunner.UexClient;

/// <summary>
/// Default implementation of <see cref="ITradeRouteProvider"/>.
///
/// Wraps <see cref="IUexApiClient.GetCommodityRoutesAsync"/> with rate-limit-safe
/// caching and enriches each row with a datarunner-specific overlay:
///  - <c>StaleAtOrigin</c>/<c>StaleAtDestination</c> = how many <see cref="StaleTarget"/>
///    rows would be refreshed by visiting each end of the route.
///  - excluded count = how many of those were already submitted locally within
///    the last 6h (and thus subtracted from the visible counters).
///
/// Cache strategy:
///  - Per-query in-memory result + persisted to disk under one JSON file per app
///    install (keyed by query). On startup we restore the most recent query's
///    proposals so the view is non-empty before the first network call.
///  - Auto TTL: 30 min (matches UEX server-side cache for /commodities_routes).
///  - Manual throttle: 60s (smaller payload than commodities_prices_all so we
///    can be more permissive).
///  - <see cref="RefreshAsync"/> ALWAYS chains a non-forced refresh of
///    <see cref="IStaleTargetProvider"/> first, so the overlay reflects external
///    submissions / UEX moderation batches up to that provider's own TTL.
/// </summary>
public sealed class TradeRouteProvider : ITradeRouteProvider
{
    private static readonly TimeSpan DefaultAutoRefreshTtl = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan DefaultManualThrottle = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan SubmissionDedupWindow = TimeSpan.FromHours(6);

    /// <summary>
    /// Minimum age (in days) for a <see cref="StaleTarget"/> row to be counted
    /// in the trade-route overlay. Matches the default Targets-view filter
    /// ("Show only over 30 days") so a row showing "↻ 44" in this column would
    /// be consistent with what the Targets list displays for the same terminal.
    /// </summary>
    private const int StaleAgeThresholdDays = 30;

    private readonly IUexApiClient _api;
    private readonly IStaleTargetProvider _stale;
    private readonly ISubmissionHistory _history;
    private readonly ILogger<TradeRouteProvider> _logger;
    private readonly string _cachePath;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    private List<TradeRouteProposal> _proposals = new();
    private TradeRouteQuery? _currentQuery;

    /// <summary>
    /// Last query for which <see cref="_proposals"/> was actually populated from
    /// the API (or restored from disk cache). Distinct from <see cref="_currentQuery"/>
    /// which reflects the user's *intent* and may differ from the latest fetch
    /// while a request is pending or right after the user changed a filter.
    /// </summary>
    private TradeRouteQuery? _lastFetchedQuery;

    private DateTimeOffset? _lastRefreshedAt;
    private DateTimeOffset _lastManualAttemptAt = DateTimeOffset.MinValue;

    public TradeRouteProvider(
        IUexApiClient api,
        IStaleTargetProvider stale,
        ISubmissionHistory history,
        ILogger<TradeRouteProvider> logger,
        string? overrideCachePath = null,
        TimeSpan? autoRefreshTtl = null,
        TimeSpan? manualThrottle = null)
    {
        _api = api;
        _stale = stale;
        _history = history;
        _logger = logger;
        _cachePath = overrideCachePath ?? DefaultCachePath();
        MinAutoRefreshInterval = autoRefreshTtl ?? DefaultAutoRefreshTtl;
        MinManualRefreshInterval = manualThrottle ?? DefaultManualThrottle;
        Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
        TryLoadFromDisk();

        // When the stale-baseline gets refreshed (either by Targets view or by
        // our own chained refresh), recompute the overlay counters in place so
        // the Trade Routes view stays in sync without a full re-fetch.
        _stale.Refreshed += OnStaleRefreshed;
    }

    public IReadOnlyList<TradeRouteProposal> Proposals => _proposals;
    public DateTimeOffset? LastRefreshedAt => _lastRefreshedAt;
    public TradeRouteQuery? CurrentQuery => _currentQuery;
    public TimeSpan MinAutoRefreshInterval { get; }
    public TimeSpan MinManualRefreshInterval { get; }
    public event EventHandler? Refreshed;

    public async Task<bool> SetQueryAndRefreshAsync(TradeRouteQuery query, bool force = false, CancellationToken ct = default)
    {
        // Just record the user's intent. RefreshAsync handles cache vs throttle
        // by comparing _currentQuery to _lastFetchedQuery — any divergence is
        // treated as a "new query" (must hit API, no manual throttle), while
        // identical queries re-use the cached result up to MinAutoRefreshInterval.
        _currentQuery = query;
        return await RefreshAsync(force, ct).ConfigureAwait(false);
    }

    public async Task<bool> RefreshAsync(bool force = false, CancellationToken ct = default)
    {
        if (_currentQuery is null)
        {
            _logger.LogDebug("TradeRouteProvider.RefreshAsync called with no current query; no-op.");
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        var sameQuery = Equals(_currentQuery, _lastFetchedQuery);

        // Manual-throttle ONLY applies when the user mashes "Refresh" on the same
        // query we already have data for. A query change (origin/dest/investment)
        // is a brand-new fetch — bypass the throttle, otherwise the user picks a
        // new origin and gets stuck on the previous origin's results for 60 s.
        if (force && sameQuery && now - _lastManualAttemptAt < MinManualRefreshInterval)
        {
            var wait = MinManualRefreshInterval - (now - _lastManualAttemptAt);
            _logger.LogInformation(
                "Trade-routes manual refresh throttled (wait {Wait:c} before next attempt).",
                wait);
            return false;
        }

        // Cache hit: same query as last successful fetch, within TTL. We honour
        // this even when _proposals is empty — UEX legitimately returns 0 routes
        // for some origin/investment combos and we must not re-hammer the API on
        // every IsVisibleChanged or filter no-op just because the result is empty.
        if (!force && sameQuery && _lastRefreshedAt is { } at && (now - at) < MinAutoRefreshInterval)
        {
            _logger.LogDebug("Trade-routes cache still fresh ({Age:c}); skip API call.", now - at);
            return false;
        }

        await _refreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-check after acquiring the lock — another thread might have just refreshed.
            sameQuery = Equals(_currentQuery, _lastFetchedQuery);
            if (!force && sameQuery && _lastRefreshedAt is { } at2 && (DateTimeOffset.UtcNow - at2) < MinAutoRefreshInterval)
            {
                return false;
            }

            if (force) _lastManualAttemptAt = DateTimeOffset.UtcNow;

            // (1) Chain a stale-baseline refresh BEFORE pulling routes, so the
            //     overlay reflects submissions made by other datarunners since
            //     our last fetch / UEX moderation batches that just landed.
            //     IStaleTargetProvider has its own TTL+throttle: this is a
            //     no-op when the cache is already fresh, and tolerates
            //     transient errors so we don't fail the whole route fetch.
            try
            {
                await _stale.RefreshAsync(force: force, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Chained stale-targets refresh failed; continuing with last known baseline.");
            }

            // (2) Fetch routes from UEX (their algo, untouched).
            //
            // We deliberately DO NOT pass `query.Investment` to UEX. The UEX
            // server treats the `investment` filter as a hard cap on the
            // route's pre-computed stock value (`scu_origin × price_origin`),
            // which silently drops every commodity whose origin terminal
            // holds more than the user's budget worth of stock — even though
            // the user could perfectly well buy a partial load.
            //
            // Example: Scrap at CRU-L1 has 2100 SCU × 2990 = 6.3M of stock.
            // With `investment=300000`, UEX returns 0 Scrap routes, leaving
            // only Waste (≤17K stock) — which is misleading because a 100-SCU
            // partial buy at 299K is a perfectly valid 161K-profit run.
            //
            // The UEX website mirrors this by capping SCU client-side. We do
            // the same in <see cref="BuildProposalsAsync"/> below: fetch
            // everything UEX will return, then compute Effective* fields per
            // proposal so the UI shows partial-load profit/investment.
            var query = _currentQuery!;
            _logger.LogInformation(
                "Refreshing trade routes from UEX (origin={Origin}/{OriginScope}, investment_cap={Inv}, commodity={Comm}, dest={Dest}/{DestScope}, force={Force})...",
                query.OriginId, query.OriginScope, query.Investment, query.IdCommodity, query.DestinationId, query.DestinationScope, force);

            var raw = await _api.GetCommodityRoutesAsync(
                originId: query.OriginId,
                originScope: query.OriginScope,
                investment: null, // see comment above — applied client-side instead
                idCommodity: query.IdCommodity,
                destinationId: query.DestinationId,
                destinationScope: query.DestinationScope,
                ct: ct)
                .ConfigureAwait(false);

            // (3) Cross-ref each row with the (now up-to-date) stale targets and
            //     our local submission history. Also project the budget-capped
            //     SCU/investment/profit fields so the UI shows partial-load
            //     economics matching the UEX website.
            var proposals = await BuildProposalsAsync(raw, query.Investment, ct).ConfigureAwait(false);

            _proposals = proposals;
            _lastFetchedQuery = _currentQuery;
            _lastRefreshedAt = DateTimeOffset.UtcNow;
            await SaveToDiskAsync(ct).ConfigureAwait(false);

            Refreshed?.Invoke(this, EventArgs.Empty);
            _logger.LogInformation(
                "Trade routes refreshed: {Count} proposals (TTL {Ttl:c}).",
                proposals.Count, MinAutoRefreshInterval);
            return true;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    /// <summary>
    /// Builds <see cref="TradeRouteProposal"/> rows from the raw UEX response,
    /// computing the stale-overlay counters per route. Submission history is
    /// queried once per terminal (not once per route) and memoised inside this
    /// scope to avoid hammering SQLite when the same terminal appears 50 times
    /// across the candidate set.
    /// </summary>
    private async Task<List<TradeRouteProposal>> BuildProposalsAsync(
        IReadOnlyList<UexCommodityRoute> raw, int? investmentCap, CancellationToken ct)
    {
        // Memoise SubmissionHistory lookups: each terminal is queried at most once.
        var recentByTerminal = new Dictionary<int, HashSet<int>>();

        async Task<HashSet<int>> RecentForAsync(int idTerminal)
        {
            if (recentByTerminal.TryGetValue(idTerminal, out var cached)) return cached;
            try
            {
                var rows = await _history.GetRecentByTerminalAsync(idTerminal, SubmissionDedupWindow, ct)
                    .ConfigureAwait(false);
                var ids = rows.Where(r => r.Ok)
                              .SelectMany(r => r.SubmittedCommodityIds)
                              .ToHashSet();
                recentByTerminal[idTerminal] = ids;
                return ids;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Could not query submission history for terminal {Term}; treating as no recent submissions.",
                    idTerminal);
                var empty = new HashSet<int>();
                recentByTerminal[idTerminal] = empty;
                return empty;
            }
        }

        // ONE filter here: IsReachable. We deliberately do NOT pre-filter by
        // DaysStale because the AGE metric (DaysStaleAtOriginThisCommodity /
        // MaxDaysStaleAtOriginAnyCommodity) must reflect what UEX shows on a
        // terminal page — a 3-day-old row should display "3d", not "0d"
        // because 3 < 30. The 30-day threshold is applied LATER, only on the
        // COUNT (StaleAtOrigin/StaleAtDestination), because a count of all
        // 100+ tracked rows would be useless noise.
        //
        // IsReachable filter (kept): drops terminals UEX has flagged
        // is_available_live=0 after a CIG patch (decommissioned / renamed /
        // purged from the catalog). Stays in sync with the Targets-view default
        // (which hides those unless "Show unreachable" is on).
        var staleByTerminal = _stale.Targets
            .Where(s => s.IsReachable)
            .GroupBy(s => s.IdTerminal)
            .ToDictionary(g => g.Key, g => g.ToList());

        var result = new List<TradeRouteProposal>(raw.Count);
        foreach (var r in raw)
        {
            var prop = new TradeRouteProposal { Route = r };

            staleByTerminal.TryGetValue(r.IdTerminalOrigin, out var staleAtO);
            staleByTerminal.TryGetValue(r.IdTerminalDestination, out var staleAtD);

            var recentO = await RecentForAsync(r.IdTerminalOrigin).ConfigureAwait(false);
            var recentD = await RecentForAsync(r.IdTerminalDestination).ConfigureAwait(false);

            // Materialise the post-exclusion list once. Includes rows of ANY
            // age — we'll split into "oldest" (any age) and "noteworthy count"
            // (≥30d) below.
            var actionableO = staleAtO?.Where(s => !recentO.Contains(s.IdCommodity)).ToList();
            var actionableD = staleAtD?.Where(s => !recentD.Contains(s.IdCommodity)).ToList();

            // ROUTE-SPECIFIC AGE → matches what UEX shows in the route grid:
            // age of THIS commodity's buy price at origin, and SELL price at
            // destination. Headline badge binds to these.
            var thisAtO = actionableO?.FirstOrDefault(s =>
                s.IdCommodity == r.IdCommodity && s.Type == StaleTargetType.Buy);
            var thisAtD = actionableD?.FirstOrDefault(s =>
                s.IdCommodity == r.IdCommodity && s.Type == StaleTargetType.Sell);
            prop.DaysStaleAtOriginThisCommodity = thisAtO?.DaysStale ?? 0;
            prop.DaysStaleAtDestinationThisCommodity = thisAtD?.DaysStale ?? 0;

            // TERMINAL-WIDE MAX AGE → "if I visit this terminal, what's the
            // oldest data anywhere here that I'd refresh?". Used in the
            // tooltip and the datarunner score, NOT in the headline badge.
            prop.MaxDaysStaleAtOriginAnyCommodity = actionableO?.Count > 0 ? actionableO.Max(s => s.DaysStale) : 0;
            prop.MaxDaysStaleAtDestinationAnyCommodity = actionableD?.Count > 0 ? actionableD.Max(s => s.DaysStale) : 0;

            // COUNT → 30-day threshold: only "actionable" rows (≥30d) are
            // counted. A terminal whose 100 rows are all 5d old gets count=0
            // because there's no real datarunner work to do there.
            prop.StaleAtOrigin = actionableO?.Count(s => s.DaysStale >= StaleAgeThresholdDays) ?? 0;
            prop.StaleAtDestination = actionableD?.Count(s => s.DaysStale >= StaleAgeThresholdDays) ?? 0;

            // Excluded by submission-history dedup (same age threshold so the
            // numbers tie out: rawCountAt30d - prop.StaleAt = excludedAt30d).
            var rawCountAtThresholdO = staleAtO?.Count(s => s.DaysStale >= StaleAgeThresholdDays) ?? 0;
            var rawCountAtThresholdD = staleAtD?.Count(s => s.DaysStale >= StaleAgeThresholdDays) ?? 0;
            prop.StaleExcludedRecentlySubmitted =
                (rawCountAtThresholdO - prop.StaleAtOrigin)
                + (rawCountAtThresholdD - prop.StaleAtDestination);

            // DatarunnerScore is computed by the view-model from the slider; keep at default 0 here.

            // Budget-capped projection. With no cap, EffectiveScu = scu_origin
            // (full stock buy). With a cap, we clamp to whatever fits, mirroring
            // the UEX website which displays partial-load profit/investment
            // instead of dropping the row entirely. Routes that can't even fit
            // a single SCU within the budget are skipped — they're not actionable.
            ApplyBudgetProjection(prop, investmentCap);
            if (prop.EffectiveScu <= 0) continue;

            result.Add(prop);
        }
        return result;
    }

    /// <summary>
    /// Computes <see cref="TradeRouteProposal.EffectiveScu"/>,
    /// <see cref="TradeRouteProposal.EffectiveInvestment"/> and
    /// <see cref="TradeRouteProposal.EffectiveProfit"/> for the user's budget cap.
    ///
    /// IMPORTANT: UEX's <c>price_margin</c> field is NOT the per-SCU UEC margin
    /// — it's the percentage <c>(dest-origin)/dest × 100</c>. The actual per-SCU
    /// UEC profit is <c>price_destination - price_origin</c>. We always
    /// recompute from prices to avoid this footgun.
    /// </summary>
    private static void ApplyBudgetProjection(TradeRouteProposal p, int? investmentCap)
    {
        var maxScu = (int)Math.Floor(p.Route.ScuOrigin);
        if (maxScu <= 0)
        {
            p.EffectiveScu = 0;
            p.EffectiveInvestment = 0;
            p.EffectiveProfit = 0;
            return;
        }

        int effectiveScu = maxScu;
        if (investmentCap is { } cap && cap > 0 && p.Route.PriceOrigin > 0)
        {
            var byBudget = (int)Math.Floor(cap / p.Route.PriceOrigin);
            effectiveScu = Math.Min(maxScu, byBudget);
        }

        var marginPerScu = p.Route.PriceDestination - p.Route.PriceOrigin;
        p.EffectiveScu = effectiveScu;
        p.EffectiveInvestment = effectiveScu * p.Route.PriceOrigin;
        p.EffectiveProfit = effectiveScu * marginPerScu;
    }

    /// <summary>
    /// Catalog refreshed (stale baseline updated) → recompute the overlay
    /// counters on the in-memory proposals without hitting /commodities_routes.
    /// </summary>
    private async void OnStaleRefreshed(object? sender, EventArgs e)
    {
        if (_proposals.Count == 0) return;
        try
        {
            // Same dual-aggregation as BuildProposalsAsync: pull every reachable
            // row (any age) so MaxDaysStale reflects the true oldest, and apply
            // the 30-day filter only on the count.
            var staleByTerminal = _stale.Targets
                .Where(s => s.IsReachable)
                .GroupBy(s => s.IdTerminal)
                .ToDictionary(g => g.Key, g => g.ToList());

            var recentByTerminal = new Dictionary<int, HashSet<int>>();
            async Task<HashSet<int>> RecentForAsync(int id)
            {
                if (recentByTerminal.TryGetValue(id, out var c)) return c;
                var rows = await _history.GetRecentByTerminalAsync(id, SubmissionDedupWindow).ConfigureAwait(false);
                var ids = rows.Where(r => r.Ok).SelectMany(r => r.SubmittedCommodityIds).ToHashSet();
                recentByTerminal[id] = ids;
                return ids;
            }

            var changed = false;
            foreach (var p in _proposals)
            {
                staleByTerminal.TryGetValue(p.Route.IdTerminalOrigin, out var staleO);
                staleByTerminal.TryGetValue(p.Route.IdTerminalDestination, out var staleD);
                var recentO = await RecentForAsync(p.Route.IdTerminalOrigin).ConfigureAwait(false);
                var recentD = await RecentForAsync(p.Route.IdTerminalDestination).ConfigureAwait(false);

                var actO = staleO?.Where(s => !recentO.Contains(s.IdCommodity)).ToList();
                var actD = staleD?.Where(s => !recentD.Contains(s.IdCommodity)).ToList();

                // Route-specific age (this commodity).
                var thisO = actO?.FirstOrDefault(s =>
                    s.IdCommodity == p.Route.IdCommodity && s.Type == StaleTargetType.Buy);
                var thisD = actD?.FirstOrDefault(s =>
                    s.IdCommodity == p.Route.IdCommodity && s.Type == StaleTargetType.Sell);
                var newRouteO = thisO?.DaysStale ?? 0;
                var newRouteD = thisD?.DaysStale ?? 0;

                // Terminal-wide max age (any commodity).
                var newMaxO = actO?.Count > 0 ? actO.Max(s => s.DaysStale) : 0;
                var newMaxD = actD?.Count > 0 ? actD.Max(s => s.DaysStale) : 0;

                // Count = ≥30d only.
                var newO = actO?.Count(s => s.DaysStale >= StaleAgeThresholdDays) ?? 0;
                var newD = actD?.Count(s => s.DaysStale >= StaleAgeThresholdDays) ?? 0;
                var rawO30 = staleO?.Count(s => s.DaysStale >= StaleAgeThresholdDays) ?? 0;
                var rawD30 = staleD?.Count(s => s.DaysStale >= StaleAgeThresholdDays) ?? 0;
                var newExcluded = (rawO30 - newO) + (rawD30 - newD);

                if (p.StaleAtOrigin != newO || p.StaleAtDestination != newD
                    || p.StaleExcludedRecentlySubmitted != newExcluded
                    || p.MaxDaysStaleAtOriginAnyCommodity != newMaxO
                    || p.MaxDaysStaleAtDestinationAnyCommodity != newMaxD
                    || p.DaysStaleAtOriginThisCommodity != newRouteO
                    || p.DaysStaleAtDestinationThisCommodity != newRouteD)
                {
                    p.StaleAtOrigin = newO;
                    p.StaleAtDestination = newD;
                    p.StaleExcludedRecentlySubmitted = newExcluded;
                    p.MaxDaysStaleAtOriginAnyCommodity = newMaxO;
                    p.MaxDaysStaleAtDestinationAnyCommodity = newMaxD;
                    p.DaysStaleAtOriginThisCommodity = newRouteO;
                    p.DaysStaleAtDestinationThisCommodity = newRouteD;
                    changed = true;
                }
            }
            if (changed) Refreshed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to recompute trade-route overlay after stale refresh.");
        }
    }

    private void TryLoadFromDisk()
    {
        try
        {
            if (!File.Exists(_cachePath)) return;
            var json = File.ReadAllText(_cachePath);
            var dto = JsonSerializer.Deserialize<CacheFileDto>(json, JsonOpts);
            if (dto?.Proposals is null) return;

            // Guard against legacy caches: previous schema used IdTerminalOrigin,
            // which won't bind to OriginId. A zero (or negative) origin ID means
            // the cache predates the scope-aware schema — drop it so we don't
            // fire bogus UEX calls with id=0.
            if (dto.Query is { } q && q.OriginId <= 0)
            {
                _logger.LogInformation("Trade-routes cache predates scope schema; discarding.");
                return;
            }

            _proposals = dto.Proposals;
            _currentQuery = dto.Query;
            _lastFetchedQuery = dto.Query;
            _lastRefreshedAt = dto.RefreshedAt;
            _logger.LogInformation(
                "Loaded {Count} trade-route proposals from disk cache (query origin={Origin}/{Scope}, age={Age}).",
                _proposals.Count,
                _currentQuery?.OriginId,
                _currentQuery?.OriginScope,
                _lastRefreshedAt is { } at ? (DateTimeOffset.UtcNow - at).ToString() : "<unknown>");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load trade-routes cache from disk; ignoring.");
        }
    }

    private async Task SaveToDiskAsync(CancellationToken ct)
    {
        try
        {
            var dto = new CacheFileDto
            {
                RefreshedAt = _lastRefreshedAt ?? DateTimeOffset.UtcNow,
                Query = _currentQuery,
                Proposals = _proposals,
            };
            var json = JsonSerializer.Serialize(dto, JsonOpts);
            await File.WriteAllTextAsync(_cachePath, json, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist trade-routes cache to disk; in-memory only.");
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private static string DefaultCachePath()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SC-DataRunnerNet",
            "trade_routes_cache.json");

    private sealed class CacheFileDto
    {
        public DateTimeOffset RefreshedAt { get; set; }
        public TradeRouteQuery? Query { get; set; }
        public List<TradeRouteProposal> Proposals { get; set; } = new();
    }
}
