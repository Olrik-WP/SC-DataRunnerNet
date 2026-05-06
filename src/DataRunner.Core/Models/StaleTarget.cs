namespace DataRunner.Core.Models;

/// <summary>
/// One actionable "stale price" target the user can tackle: a (terminal, commodity)
/// pair whose <see cref="LastUpdatedAt"/> is older than the freshness threshold.
///
/// Built by the <c>IStaleTargetProvider</c> from <see cref="UexCommodityPriceAll"/>.
/// Optimised for display in a sortable / filterable DataGrid.
/// </summary>
public sealed class StaleTarget
{
    /// <summary>UEX terminal id — same as <c>id_terminal</c> in the API.</summary>
    public required int IdTerminal { get; init; }
    public required int IdCommodity { get; init; }

    public required string TerminalName { get; init; }
    public required string CommodityName { get; init; }

    /// <summary>
    /// Star system the terminal belongs to (e.g. "Stanton", "Pyro").
    /// Not present in the raw <c>commodities_prices_all</c> payload — enriched
    /// from the terminals catalog (<see cref="UexTerminal.StarSystemName"/>).
    /// May be empty if the catalog hasn't been refreshed yet.
    /// Mutable on purpose so the provider can backfill it after disk-load
    /// or after the catalog is refreshed, without rebuilding every record.
    /// </summary>
    public string? StarSystemName { get; set; }

    /// <summary>
    /// True when the terminal still exists in the current LIVE build (i.e. the
    /// player can physically fly there and update prices). False when the
    /// terminal is missing from the catalog (purged) or marked
    /// <c>is_available_live = 0</c> by UEX (decommissioned / renamed across
    /// patches). UEX still returns 400+ day-old rows for those phantom
    /// terminals via <c>commodities_prices_all</c>; we hide them by default
    /// because no datarunner can refresh them.
    ///
    /// Mutable on purpose: enriched in <c>StaleTargetProvider.EnrichWithCatalog</c>
    /// the same way as <see cref="StarSystemName"/>, so we can re-evaluate after
    /// a delayed catalog refresh without rebuilding the whole list.
    /// </summary>
    public bool IsReachable { get; set; } = true;

    /// <summary>BUY (terminal sells to player) or SELL (player sells to terminal).</summary>
    public required StaleTargetType Type { get; init; }

    /// <summary>Last known price in aUEC at this terminal for this commodity / direction.</summary>
    public required double LastKnownPrice { get; init; }

    /// <summary>UTC timestamp of the last accepted data_submit for this row.</summary>
    public required DateTimeOffset LastUpdatedAt { get; init; }

    /// <summary>Days elapsed since <see cref="LastUpdatedAt"/> (cached at materialisation time).</summary>
    public required int DaysStale { get; init; }

    /// <summary>
    /// Composite priority score (higher = more important to update). Combines
    /// staleness with proxies for "is this row useful?" (active stock / volatility).
    /// Currently a simple linear function — easy to tune later.
    /// </summary>
    public required double PriorityScore { get; init; }
}

public enum StaleTargetType
{
    Buy,
    Sell,
}
