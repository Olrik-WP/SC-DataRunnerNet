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

    /// <summary>
    /// Case-insensitive set of terminal display names that exist in MORE THAN ONE
    /// star system (e.g. "Pyro Gateway" in Stanton AND in Pyro). The UI uses this
    /// to force the user to explicitly disambiguate before submitting — a major
    /// source of bad data per UEX community feedback.
    /// </summary>
    IReadOnlySet<string> AmbiguousTerminalNames { get; }

    /// <summary>True if the given terminal's display name is ambiguous (see above).</summary>
    bool IsAmbiguous(UexTerminal terminal);

    UexCommodity? GetCommodity(int id);
    UexTerminal? GetTerminal(int id);

    Task<bool> RefreshAsync(bool force = false, CancellationToken ct = default);

    /// <summary>Raised on the UI thread after a successful refresh.</summary>
    event EventHandler? Refreshed;
}
