using DataRunner.Core.Models;

namespace DataRunner.Core.Abstractions;

/// <summary>
/// Read-only snapshot of UEX cargo vehicles, used to populate the vehicle-selection
/// combo on the Trade Routes view and to constrain routes by container_sizes.
///
/// IMPLEMENTATION CONTRACT:
///  - Backed by a local on-disk cache (24h TTL, matches UEX's 12h server cache + buffer).
///  - Auto-fetches once per app session if cache is stale; otherwise serves from disk.
///  - Exposes ONLY cargo-capable vehicles (<c>is_cargo == 1 &amp;&amp; scu &gt; 0</c>) since those
///    are the only ones a datarunner cares about when planning trade routes.
/// </summary>
public interface IVehicleCatalog
{
    /// <summary>Snapshot of cargo-capable vehicles, sorted by SCU descending then by name.</summary>
    IReadOnlyList<UexVehicle> CargoVehicles { get; }

    /// <summary>UTC timestamp of the last successful refresh, or null if never fetched.</summary>
    DateTimeOffset? LastRefreshedAt { get; }

    /// <summary>
    /// Refreshes the catalog. Returns true if the API was actually hit, false if
    /// served from cache (TTL still valid) or if a concurrent refresh is running.
    /// </summary>
    Task<bool> RefreshAsync(bool force = false, CancellationToken ct = default);

    /// <summary>Raised on the UI thread after a successful refresh.</summary>
    event EventHandler? Refreshed;
}
