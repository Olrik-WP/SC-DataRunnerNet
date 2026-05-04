using System.Text.Json;
using DataRunner.Core.Abstractions;
using DataRunner.Core.Models;
using Microsoft.Extensions.Logging;

namespace DataRunner.UexClient;

/// <summary>
/// Default implementation of <see cref="IStaleTargetProvider"/>.
///
/// Backed by a single JSON cache file on disk. Hits the API at most once every
/// <see cref="MinAutoRefreshInterval"/> (default 6h) — and a manual refresh from
/// the UI can override the TTL but is still throttled to <see cref="MinManualRefreshInterval"/>
/// (default 5min) so that mashing the button does not spam UEX.
///
/// Persisted file: %LOCALAPPDATA%\SC-DataRunnerNet\stale_targets_cache.json
/// </summary>
public sealed class StaleTargetProvider : IStaleTargetProvider
{
    private static readonly TimeSpan DefaultAutoRefreshTtl = TimeSpan.FromHours(6);
    private static readonly TimeSpan DefaultManualThrottle = TimeSpan.FromMinutes(5);

    private readonly IUexApiClient _api;
    private readonly ICatalogProvider _catalog;
    private readonly ILogger<StaleTargetProvider> _logger;
    private readonly string _cachePath;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    private List<StaleTarget> _targets = new();
    private DateTimeOffset? _lastRefreshedAt;
    private DateTimeOffset _lastManualAttemptAt = DateTimeOffset.MinValue;

    public StaleTargetProvider(
        IUexApiClient api,
        ICatalogProvider catalog,
        ILogger<StaleTargetProvider> logger,
        string? overrideCachePath = null,
        TimeSpan? autoRefreshTtl = null,
        TimeSpan? manualThrottle = null)
    {
        _api = api;
        _catalog = catalog;
        _logger = logger;
        _cachePath = overrideCachePath ?? DefaultCachePath();
        MinAutoRefreshInterval = autoRefreshTtl ?? DefaultAutoRefreshTtl;
        MinManualRefreshInterval = manualThrottle ?? DefaultManualThrottle;
        Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
        TryLoadFromDisk();

        // The catalog may finish loading AFTER we already restored stale targets
        // from disk (or after the API refresh). When that happens, backfill the
        // StarSystemName on every existing target and notify the UI.
        _catalog.Refreshed += OnCatalogRefreshed;
    }

    private void OnCatalogRefreshed(object? sender, EventArgs e)
    {
        if (_targets.Count == 0) return;
        if (EnrichWithCatalog(_targets))
            Refreshed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Looks up <see cref="StaleTarget.StarSystemName"/> for every record from the
    /// terminals catalog. Mutates the list in place. Returns true if at least one
    /// record was updated, so callers can decide whether to re-broadcast Refreshed.
    /// </summary>
    private bool EnrichWithCatalog(IList<StaleTarget> targets)
    {
        var changed = false;
        foreach (var t in targets)
        {
            var terminal = _catalog.GetTerminal(t.IdTerminal);
            var sys = terminal?.StarSystemName;
            if (!string.Equals(t.StarSystemName, sys, StringComparison.Ordinal))
            {
                t.StarSystemName = sys;
                changed = true;
            }
        }
        return changed;
    }

    public IReadOnlyList<StaleTarget> Targets => _targets;
    public DateTimeOffset? LastRefreshedAt => _lastRefreshedAt;
    public TimeSpan MinAutoRefreshInterval { get; }
    public TimeSpan MinManualRefreshInterval { get; }

    public event EventHandler? Refreshed;

    public async Task<bool> RefreshAsync(bool force = false, CancellationToken ct = default)
    {
        // Manual throttle (always applies, even with force=true).
        // Prevents a user mashing "Refresh" from generating dozens of requests.
        var now = DateTimeOffset.UtcNow;
        if (force && now - _lastManualAttemptAt < MinManualRefreshInterval)
        {
            var wait = MinManualRefreshInterval - (now - _lastManualAttemptAt);
            _logger.LogInformation(
                "Stale-targets manual refresh throttled (wait {Wait:c} before next attempt).",
                wait);
            return false;
        }

        // Auto-refresh TTL guard (only when not forced).
        if (!force && _lastRefreshedAt is { } at && (now - at) < MinAutoRefreshInterval && _targets.Count > 0)
        {
            _logger.LogDebug(
                "Stale-targets cache still fresh ({Age:c}); skip API call.",
                now - at);
            return false;
        }

        await _refreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-check after acquiring the lock — another thread might have just refreshed.
            if (!force && _lastRefreshedAt is { } at2 && (DateTimeOffset.UtcNow - at2) < MinAutoRefreshInterval && _targets.Count > 0)
            {
                return false;
            }

            if (force) _lastManualAttemptAt = DateTimeOffset.UtcNow;

            _logger.LogInformation("Refreshing stale targets from UEX API (force={Force})...", force);
            var raw = await _api.GetAllCommodityPricesAsync(ct).ConfigureAwait(false);

            var targets = BuildTargets(raw, DateTimeOffset.UtcNow);
            EnrichWithCatalog(targets);
            _targets = targets;
            _lastRefreshedAt = DateTimeOffset.UtcNow;

            await SaveToDiskAsync(ct).ConfigureAwait(false);

            Refreshed?.Invoke(this, EventArgs.Empty);
            _logger.LogInformation(
                "Stale targets refreshed: {Total} actionable rows (cache TTL {Ttl:c}).",
                targets.Count, MinAutoRefreshInterval);
            return true;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    /// <summary>
    /// Folds the raw API response into one <see cref="StaleTarget"/> per non-empty
    /// (terminal, commodity, direction) combination. A row that has BOTH a buy
    /// price and a sell price (rare for commodities) yields two targets.
    /// </summary>
    private static List<StaleTarget> BuildTargets(IReadOnlyList<UexCommodityPriceAll> rows, DateTimeOffset now)
    {
        var list = new List<StaleTarget>(rows.Count);

        foreach (var r in rows)
        {
            if (r.DateModified <= 0) continue;

            var lastUpdated = DateTimeOffset.FromUnixTimeSeconds(r.DateModified);
            var daysStale = (int)Math.Floor((now - lastUpdated).TotalDays);

            if (r.PriceBuy > 0)
            {
                list.Add(new StaleTarget
                {
                    IdTerminal = r.IdTerminal,
                    IdCommodity = r.IdCommodity,
                    TerminalName = r.TerminalName,
                    CommodityName = r.CommodityName,
                    Type = StaleTargetType.Buy,
                    LastKnownPrice = r.PriceBuy,
                    LastUpdatedAt = lastUpdated,
                    DaysStale = daysStale,
                    PriorityScore = ComputePriority(daysStale, r.ScuBuy, r.StatusBuy),
                });
            }

            if (r.PriceSell > 0)
            {
                list.Add(new StaleTarget
                {
                    IdTerminal = r.IdTerminal,
                    IdCommodity = r.IdCommodity,
                    TerminalName = r.TerminalName,
                    CommodityName = r.CommodityName,
                    Type = StaleTargetType.Sell,
                    LastKnownPrice = r.PriceSell,
                    LastUpdatedAt = lastUpdated,
                    DaysStale = daysStale,
                    PriorityScore = ComputePriority(daysStale, r.ScuSellStock, r.StatusSell),
                });
            }
        }

        return list.OrderByDescending(t => t.PriorityScore).ToList();
    }

    /// <summary>
    /// Naive priority score. Tunable later. Goal: front-load rows that are BOTH
    /// stale AND likely-to-be-traded.
    ///
    ///   score = days_stale × (1 + log10(1 + stock)) × statusBoost
    ///
    ///   - days_stale     : the more days since update, the higher
    ///   - stock factor   : a row with no known stock (0 SCU) gets a tiny bonus only;
    ///                      a row with 10000 SCU gets a much bigger one (popular trade lane)
    ///   - statusBoost    : status == 0 (unknown) → 0.7;  1..7 → 1.0..1.4 (more reliable rows)
    /// </summary>
    private static double ComputePriority(int daysStale, double stock, int statusCode)
    {
        var stockFactor = 1.0 + Math.Log10(1.0 + Math.Max(0, stock));
        var statusBoost = statusCode <= 0 ? 0.7 : 1.0 + (statusCode / 10.0);
        return Math.Max(1, daysStale) * stockFactor * statusBoost;
    }

    private void TryLoadFromDisk()
    {
        try
        {
            if (!File.Exists(_cachePath)) return;

            var json = File.ReadAllText(_cachePath);
            var dto = JsonSerializer.Deserialize<CacheFileDto>(json, JsonOpts);
            if (dto is null) return;

            _targets = dto.Targets ?? new();
            _lastRefreshedAt = dto.RefreshedAt;

            // Older cache files (pre-system-column) didn't persist StarSystemName
            // on each row. Backfill from the catalog on load — if the catalog
            // is itself still cold, the second pass via OnCatalogRefreshed will
            // catch up. Either way the user sees system info as soon as it's known.
            EnrichWithCatalog(_targets);

            _logger.LogInformation(
                "Loaded {Count} stale targets from disk cache ({Age:c} old).",
                _targets.Count,
                _lastRefreshedAt is { } at ? DateTimeOffset.UtcNow - at : TimeSpan.Zero);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load stale-targets cache from disk; ignoring.");
        }
    }

    private async Task SaveToDiskAsync(CancellationToken ct)
    {
        try
        {
            var dto = new CacheFileDto
            {
                RefreshedAt = _lastRefreshedAt ?? DateTimeOffset.UtcNow,
                Targets = _targets,
            };
            var json = JsonSerializer.Serialize(dto, JsonOpts);
            await File.WriteAllTextAsync(_cachePath, json, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist stale-targets cache to disk; in-memory only.");
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        IncludeFields = false,
    };

    private static string DefaultCachePath()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SC-DataRunnerNet",
            "stale_targets_cache.json");

    private sealed class CacheFileDto
    {
        public DateTimeOffset RefreshedAt { get; set; }
        public List<StaleTarget> Targets { get; set; } = new();
    }
}
