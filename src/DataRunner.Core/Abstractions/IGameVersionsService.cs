using DataRunner.Core.Models;

namespace DataRunner.Core.Abstractions;

/// <summary>
/// Caches the Star Citizen build numbers UEX accepts in /data_submit's
/// <c>game_version</c> field. Backed by GET /game_versions on the UEX 2.0 API.
///
/// Caches with a 24h TTL (matches UEX's documented server-side cache) and
/// persists the last successful payload to disk so the app can resolve
/// versions even when offline at startup.
///
/// All public methods are safe to call from any thread; the implementation
/// guards refreshes with a single-shot lock.
/// </summary>
public interface IGameVersionsService
{
    /// <summary>
    /// Last successfully fetched (or persisted) versions, or <c>null</c>
    /// when the service has never seen a payload — neither online, nor
    /// from a cached file. Use <see cref="ResolveAsync"/> instead of
    /// reading this directly when you want a string suitable for the
    /// /data_submit payload.
    /// </summary>
    UexGameVersions? Cached { get; }

    /// <summary>
    /// True when at least one successful fetch (or disk-cache load) has
    /// populated <see cref="Cached"/> in this process lifetime.
    /// </summary>
    bool HasCache { get; }

    /// <summary>
    /// Resolves the build number string to send for the given branch.
    /// Triggers a refresh if the in-memory cache is older than the TTL or
    /// missing entirely.
    ///
    /// Returns:
    ///   - the cached build number ("4.7.2" / "4.8.0") on success;
    ///   - the literal "LIVE" or "PTU" as a documented UEX-accepted fallback
    ///     when the network is unreachable AND we have no on-disk cache;
    ///   - <c>null</c> ONLY when <paramref name="branch"/> is PTU and UEX
    ///     reports no PTU build is currently published — callers should
    ///     surface a friendly error in that case.
    /// </summary>
    Task<string?> ResolveAsync(GameBranch branch, CancellationToken ct = default);

    /// <summary>
    /// Forces a network fetch regardless of cache state. Used by Settings'
    /// "Refresh from UEX now" button so the user can verify connectivity.
    /// Throws on network / API errors so the UI can show the failure.
    /// </summary>
    Task<UexGameVersions> RefreshAsync(CancellationToken ct = default);

    /// <summary>
    /// Loads the last successful payload from disk (if any) without hitting
    /// the network. Called once on app startup so subsequent
    /// <see cref="ResolveAsync"/> calls have a usable fallback even before
    /// the first network refresh completes.
    /// </summary>
    Task LoadFromDiskAsync(CancellationToken ct = default);
}
