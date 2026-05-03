using DataRunner.Core.Models;

namespace DataRunner.Core.Abstractions;

/// <summary>
/// Read-only snapshot of the UEX catalog (commodities + commodity terminals).
/// Refreshed by a background CatalogRefresher service.
/// </summary>
public interface ICatalogProvider
{
    DateTimeOffset? LastRefreshedAt { get; }
    IReadOnlyList<UexCommodity> Commodities { get; }
    IReadOnlyList<UexTerminal> CommodityTerminals { get; }

    UexCommodity? GetCommodity(int id);
    UexTerminal? GetTerminal(int id);

    Task<bool> RefreshAsync(bool force = false, CancellationToken ct = default);

    /// <summary>Raised on the UI thread after a successful refresh.</summary>
    event EventHandler? Refreshed;
}
