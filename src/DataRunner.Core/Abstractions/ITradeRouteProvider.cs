using DataRunner.Core.Models;

namespace DataRunner.Core.Abstractions;

/// <summary>
/// Provides datarunner-aware trade route proposals derived from UEX's
/// <c>commodities_routes</c> endpoint, enriched with our local stale-target
/// overlay and dedup'd against recent submissions.
///
/// IMPLEMENTATION CONTRACT (rate-limit safe):
///  - Backed by a local cache (JSON on disk, keyed by query) so repeated lookups
///    on the same origin/investment do not hit the API.
///  - Auto-refresh ONLY if the cache for that query is older than
///    <see cref="MinAutoRefreshInterval"/> (default 30 min, matches UEX server TTL).
///  - Manual refresh ALWAYS allowed but throttled to <see cref="MinManualRefreshInterval"/>
///    (default 60s — smaller dataset than commodities_prices_all).
///  - Refresh ALSO chains a non-forced refresh of <c>IStaleTargetProvider</c> so
///    the stale-overlay reflects the latest known UEX prices (within that
///    provider's own TTL/throttle).
/// </summary>
public interface ITradeRouteProvider
{
    /// <summary>
    /// Snapshot of the most recently fetched route proposals (already enriched
    /// with stale-overlay and ordered as returned by UEX).
    /// </summary>
    IReadOnlyList<TradeRouteProposal> Proposals { get; }

    /// <summary>UTC timestamp of the last successful refresh of the current query, or null if never fetched.</summary>
    DateTimeOffset? LastRefreshedAt { get; }

    /// <summary>The query in effect for the current <see cref="Proposals"/> snapshot.</summary>
    TradeRouteQuery? CurrentQuery { get; }

    TimeSpan MinAutoRefreshInterval { get; }
    TimeSpan MinManualRefreshInterval { get; }

    /// <summary>
    /// Sets the active query and refreshes proposals. Re-uses the cached result
    /// when the same <paramref name="query"/> was fetched within
    /// <see cref="MinAutoRefreshInterval"/> unless <paramref name="force"/> is true.
    /// Returns true if the API was actually hit, false otherwise (cache hit or throttled).
    /// </summary>
    Task<bool> SetQueryAndRefreshAsync(TradeRouteQuery query, bool force = false, CancellationToken ct = default);

    /// <summary>Refreshes the CURRENT query (no-op if no query has been set yet).</summary>
    Task<bool> RefreshAsync(bool force = false, CancellationToken ct = default);

    /// <summary>Raised on the UI thread after a successful refresh.</summary>
    event EventHandler? Refreshed;
}

/// <summary>
/// Geographic scope of an origin / destination ID in a <see cref="TradeRouteQuery"/>.
///
/// UEX's <c>/commodities_routes</c> accepts four mutually-exclusive origin
/// filters: <c>id_terminal_origin</c>, <c>id_orbit_origin</c>, <c>id_planet_origin</c>
/// and <c>id_commodity</c>. Picking <see cref="Terminal"/> matches a single
/// shop; picking <see cref="Orbit"/> aggregates every terminal at e.g. CRU-L1
/// (this is what the UEX website does when you click a location). <see cref="Planet"/>
/// goes wider still.
/// </summary>
public enum RouteScope
{
    Terminal = 0,
    Orbit = 1,
    Planet = 2,
}

/// <summary>
/// Query parameters mirroring the UEX <c>/commodities_routes</c> input set.
/// At least one origin filter is required. Investment caps the routes by total
/// UEC budget. <see cref="OriginScope"/> selects which UEX filter is sent for
/// the origin ID.
/// </summary>
public sealed record TradeRouteQuery(
    int OriginId,
    RouteScope OriginScope = RouteScope.Terminal,
    int? Investment = null,
    int? IdCommodity = null,
    int? DestinationId = null,
    RouteScope DestinationScope = RouteScope.Terminal);
