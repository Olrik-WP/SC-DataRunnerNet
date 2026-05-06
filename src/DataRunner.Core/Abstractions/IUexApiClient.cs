using DataRunner.Core.Models;

namespace DataRunner.Core.Abstractions;

/// <summary>Thin wrapper around the UEX 2.0 REST API.</summary>
public interface IUexApiClient
{
    Task<IReadOnlyList<UexCommodity>> GetCommoditiesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<UexTerminal>> GetCommodityTerminalsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<UexCommodityRawPrice>> GetCommodityRawPricesAsync(int idTerminal, CancellationToken ct = default);

    /// <summary>
    /// GET /commodities_prices_all — single call returning the entire universe of
    /// active commodity prices (~1 MB). Used by the Stale Targets feature.
    /// CALLERS MUST CACHE — at minimum 1h, ideally 6h+.
    /// </summary>
    Task<IReadOnlyList<UexCommodityPriceAll>> GetAllCommodityPricesAsync(CancellationToken ct = default);

    /// <summary>
    /// GET /game_versions — returns the Star Citizen build numbers UEX
    /// currently accepts in /data_submit's <c>game_version</c> field.
    /// Cache TTL is 1 day server-side; clients should respect it.
    /// </summary>
    Task<UexGameVersions> GetGameVersionsAsync(CancellationToken ct = default);

    /// <summary>POST /data_submit. Returns the raw API response body.</summary>
    Task<UexSubmitResult> SubmitDataAsync(UexDataSubmitPayload payload, CancellationToken ct = default);

    /// <summary>
    /// GET /commodities_routes — UEX's pre-calculated trade routes from a given
    /// origin, optionally constrained by investment cap, commodity, or destination.
    /// The <paramref name="originScope"/> selects which UEX filter to apply for
    /// the origin ID (terminal / orbit / planet) — using <see cref="RouteScope.Orbit"/>
    /// aggregates all sibling terminals at that orbit (mirrors what the UEX
    /// website shows when you browse a location), whereas
    /// <see cref="RouteScope.Terminal"/> restricts the result to a single shop.
    /// Returns up to 500 rows. Server cache TTL: 30 min. Update frequency: hourly.
    /// </summary>
    Task<IReadOnlyList<UexCommodityRoute>> GetCommodityRoutesAsync(
        int originId,
        RouteScope originScope = RouteScope.Terminal,
        int? investment = null,
        int? idCommodity = null,
        int? destinationId = null,
        RouteScope destinationScope = RouteScope.Terminal,
        CancellationToken ct = default);

    /// <summary>
    /// GET /vehicles — full vehicle catalog (ships and ground vehicles).
    /// Server cache TTL is 12h. Callers should cache locally for 24h+.
    /// </summary>
    Task<IReadOnlyList<UexVehicle>> GetVehiclesAsync(CancellationToken ct = default);
}

/// <summary>Result of a POST /data_submit request.</summary>
public sealed record UexSubmitResult(
    bool Ok,
    int HttpStatusCode,
    string? Status,
    string? Message,
    string RawResponseBody,
    string SerialisedRequestBody);
