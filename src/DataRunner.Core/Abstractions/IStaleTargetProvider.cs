using DataRunner.Core.Models;

namespace DataRunner.Core.Abstractions;

/// <summary>
/// Provides the list of "stale" UEX commodity targets — (terminal, commodity) pairs
/// that haven't been updated for a while and would benefit from a fresh data_submit.
///
/// IMPLEMENTATION CONTRACT (rate-limit safe):
///  - Backed by a local cache (JSON on disk) so app restarts do NOT hit the API.
///  - Auto-refresh ONLY if the cache is older than <see cref="MinAutoRefreshInterval"/>.
///  - Manual refresh ALWAYS allowed but throttled to <see cref="MinManualRefreshInterval"/>
///    (returns <c>false</c> from <see cref="RefreshAsync"/> if called too often).
///  - Single endpoint used: <c>GET /commodities_prices_all</c> (one call ≈ 1 MB,
///    yields the entire universe). At 6h cache that's 4 calls/day = 0.002% of quota.
/// </summary>
public interface IStaleTargetProvider
{
    /// <summary>Snapshot of the current stale targets (already sorted by PriorityScore desc).</summary>
    IReadOnlyList<StaleTarget> Targets { get; }

    /// <summary>UTC timestamp of the last successful refresh, or null if never fetched.</summary>
    DateTimeOffset? LastRefreshedAt { get; }

    /// <summary>Floor TTL for the on-disk cache. The provider will not auto-fetch below this.</summary>
    TimeSpan MinAutoRefreshInterval { get; }

    /// <summary>Minimum delay between two manual <see cref="RefreshAsync"/> calls.</summary>
    TimeSpan MinManualRefreshInterval { get; }

    /// <summary>
    /// Refreshes the target list.
    /// Returns <c>true</c> if the API was actually hit, <c>false</c> if served from cache
    /// or if throttled.
    /// </summary>
    /// <param name="force">If true, bypasses the auto-refresh TTL but still respects the manual throttle.</param>
    Task<bool> RefreshAsync(bool force = false, CancellationToken ct = default);

    /// <summary>Raised on the UI thread after a successful refresh.</summary>
    event EventHandler? Refreshed;
}
