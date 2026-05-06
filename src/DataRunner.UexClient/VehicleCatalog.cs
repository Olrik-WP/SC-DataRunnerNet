using System.Text.Json;
using DataRunner.Core.Abstractions;
using DataRunner.Core.Models;
using Microsoft.Extensions.Logging;

namespace DataRunner.UexClient;

/// <summary>
/// Default <see cref="IVehicleCatalog"/>: hits <c>GET /vehicles</c> at most once
/// every 24h and persists the cargo-capable subset to a JSON file at
/// <c>%LOCALAPPDATA%\SC-DataRunnerNet\vehicles.cache.json</c> for offline access.
///
/// We keep ONLY <c>is_cargo == 1 &amp;&amp; scu &gt; 0</c> rows because the Trade
/// Routes view's vehicle combo is meaningless for non-cargo vehicles. This
/// reduces the cached file size ~5x and the combo length to a sensible ~30 ships.
/// </summary>
public sealed class VehicleCatalog : IVehicleCatalog
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private readonly IUexApiClient _api;
    private readonly ILogger<VehicleCatalog> _logger;
    private readonly string _cacheFilePath;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    private List<UexVehicle> _cargoVehicles = new();
    private DateTimeOffset? _fetchedAt;

    public VehicleCatalog(
        IUexApiClient api,
        ILogger<VehicleCatalog> logger,
        string? cacheFileOverride = null)
    {
        _api = api;
        _logger = logger;
        _cacheFilePath = cacheFileOverride ?? DefaultCachePath();
        TryLoadFromDisk();
    }

    public IReadOnlyList<UexVehicle> CargoVehicles => _cargoVehicles;
    public DateTimeOffset? LastRefreshedAt => _fetchedAt;
    public event EventHandler? Refreshed;

    public async Task<bool> RefreshAsync(bool force = false, CancellationToken ct = default)
    {
        if (!force && _fetchedAt is { } at && (DateTimeOffset.UtcNow - at) < CacheTtl && _cargoVehicles.Count > 0)
        {
            return false;
        }

        await _refreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!force && _fetchedAt is { } at2 && (DateTimeOffset.UtcNow - at2) < CacheTtl && _cargoVehicles.Count > 0)
            {
                return false;
            }

            _logger.LogInformation("Refreshing UEX vehicles catalog (force={Force})...", force);
            var raw = await _api.GetVehiclesAsync(ct).ConfigureAwait(false);

            // Cargo-only filter + stable sort (SCU desc, then name asc).
            var filtered = raw
                .Where(v => v.IsCargo == 1 && v.Scu > 0)
                .OrderByDescending(v => v.Scu)
                .ThenBy(v => v.NameFull, StringComparer.OrdinalIgnoreCase)
                .ToList();

            _cargoVehicles = filtered;
            _fetchedAt = DateTimeOffset.UtcNow;
            await SaveToDiskAsync(ct).ConfigureAwait(false);

            Refreshed?.Invoke(this, EventArgs.Empty);
            _logger.LogInformation(
                "Vehicles catalog refreshed: {Count} cargo-capable rows out of {Total}.",
                filtered.Count, raw.Count);
            return true;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private void TryLoadFromDisk()
    {
        try
        {
            if (!File.Exists(_cacheFilePath)) return;
            var json = File.ReadAllText(_cacheFilePath);
            var dto = JsonSerializer.Deserialize<CacheDto>(json, JsonOpts);
            if (dto is null) return;
            _cargoVehicles = dto.Vehicles ?? new();
            _fetchedAt = dto.FetchedAt;
            _logger.LogInformation(
                "Loaded {Count} cargo vehicles from disk cache (age={Age}).",
                _cargoVehicles.Count,
                _fetchedAt is { } at ? (DateTimeOffset.UtcNow - at).ToString() : "<unknown>");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load vehicles cache from disk; ignoring.");
        }
    }

    private async Task SaveToDiskAsync(CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cacheFilePath)!);
            var dto = new CacheDto
            {
                FetchedAt = _fetchedAt ?? DateTimeOffset.UtcNow,
                Vehicles = _cargoVehicles,
            };
            var json = JsonSerializer.Serialize(dto, JsonOpts);
            await File.WriteAllTextAsync(_cacheFilePath, json, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist vehicles cache to disk; in-memory only.");
        }
    }

    private static string DefaultCachePath()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SC-DataRunnerNet",
            "vehicles.cache.json");

    private sealed class CacheDto
    {
        public DateTimeOffset FetchedAt { get; set; }
        public List<UexVehicle> Vehicles { get; set; } = new();
    }
}
