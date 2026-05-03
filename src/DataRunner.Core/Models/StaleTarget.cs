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
