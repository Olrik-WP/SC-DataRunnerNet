using System.Text.Json;
using DataRunner.Core.Abstractions;
using DataRunner.Core.Models;
using Microsoft.Extensions.Logging;

namespace DataRunner.UexClient;

/// <summary>
/// Default <see cref="IGameVersionsService"/> backed by <see cref="IUexApiClient"/>
/// + a JSON file at %LOCALAPPDATA%\SC-DataRunnerNet\game_versions.cache.json.
///
/// The cache file is non-secret (just two version strings + timestamp) so it
/// lives next to <c>prefs.json</c> rather than in the DPAPI vault.
///
/// TTL is 24h (UEX's documented server-side cache TTL); we still answer from
/// the in-memory copy when the network is unreachable, and we ALWAYS try to
/// reuse the on-disk copy at startup before the first network call lands.
/// </summary>
public sealed class GameVersionsService : IGameVersionsService
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly IUexApiClient _api;
    private readonly ILogger<GameVersionsService> _logger;
    private readonly string _cacheFilePath;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private UexGameVersions? _cached;
    private DateTimeOffset? _fetchedAt;

    public GameVersionsService(
        IUexApiClient api,
        ILogger<GameVersionsService> logger,
        string? cacheFileOverride = null)
    {
        _api = api;
        _logger = logger;
        _cacheFilePath = cacheFileOverride ?? DefaultCachePath();
    }

    public UexGameVersions? Cached => _cached;
    public bool HasCache => _cached is not null;

    public async Task<UexGameVersions> RefreshAsync(CancellationToken ct = default)
    {
        await _refreshLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _logger.LogInformation("GET /game_versions (forced refresh)");
            var fresh = await _api.GetGameVersionsAsync(ct).ConfigureAwait(false);
            _cached = fresh;
            _fetchedAt = DateTimeOffset.UtcNow;
            await PersistAsync(fresh, ct).ConfigureAwait(false);
            _logger.LogInformation(
                "Game versions refreshed: live={Live}, ptu={Ptu}",
                fresh.Live ?? "<null>", fresh.Ptu ?? "<null>");
            return fresh;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public async Task<string?> ResolveAsync(GameBranch branch, CancellationToken ct = default)
    {
        await EnsureFreshAsync(ct).ConfigureAwait(false);

        if (_cached is { } c)
        {
            return branch switch
            {
                GameBranch.Live => string.IsNullOrWhiteSpace(c.Live) ? "LIVE" : c.Live!.Trim(),
                GameBranch.Ptu => string.IsNullOrWhiteSpace(c.Ptu) ? null : c.Ptu!.Trim(),
                _ => "LIVE",
            };
        }

        // No cache, no network: fall back to the literal strings UEX accepts
        // (per the /data_submit doc: "LIVE or PTU accepted only"). For PTU we
        // still return null when the cache missed entirely — callers should
        // either let the user type a value manually or block the submission.
        return branch switch
        {
            GameBranch.Live => "LIVE",
            GameBranch.Ptu => null,
            _ => "LIVE",
        };
    }

    public async Task LoadFromDiskAsync(CancellationToken ct = default)
    {
        await _refreshLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_cacheFilePath)) return;
            await using var fs = File.OpenRead(_cacheFilePath);
            var dto = await JsonSerializer.DeserializeAsync<CacheDto>(fs, JsonOpts, ct).ConfigureAwait(false);
            if (dto?.Data is null) return;
            _cached = dto.Data;
            _fetchedAt = dto.FetchedAt;
            _logger.LogInformation(
                "Loaded cached game_versions from disk (live={Live}, ptu={Ptu}, age={Age}).",
                _cached.Live ?? "<null>", _cached.Ptu ?? "<null>",
                _fetchedAt is { } at ? (DateTimeOffset.UtcNow - at).ToString() : "<unknown>");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load cached game_versions from {Path}.", _cacheFilePath);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task EnsureFreshAsync(CancellationToken ct)
    {
        var stale = _fetchedAt is null
            || (DateTimeOffset.UtcNow - _fetchedAt.Value) > CacheTtl;
        if (!stale) return;

        try
        {
            await RefreshAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Network or API error: keep whatever in-memory cache we have
            // (possibly nothing), don't bubble. Resolve() falls back to
            // literal "LIVE" / null for PTU when there's no cache.
            _logger.LogWarning(ex,
                "Could not refresh game_versions from UEX; using cached={HasCache} fallback.",
                HasCache);
        }
    }

    private async Task PersistAsync(UexGameVersions data, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cacheFilePath)!);
            var dto = new CacheDto { FetchedAt = DateTimeOffset.UtcNow, Data = data };
            var json = JsonSerializer.Serialize(dto, JsonOpts);
            await File.WriteAllTextAsync(_cacheFilePath, json, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not persist game_versions cache to {Path}.", _cacheFilePath);
        }
    }

    private static string DefaultCachePath()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SC-DataRunnerNet",
            "game_versions.cache.json");

    private sealed class CacheDto
    {
        public DateTimeOffset FetchedAt { get; set; }
        public UexGameVersions? Data { get; set; }
    }
}
