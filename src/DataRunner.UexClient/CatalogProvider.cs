using System.Text.Json;
using DataRunner.Core.Abstractions;
using DataRunner.Core.Models;
using Microsoft.Extensions.Logging;

namespace DataRunner.UexClient;

/// <summary>
/// In-memory cache of UEX commodities + commodity terminals.
/// Persists to disk (JSON) so the UI is functional offline once a first refresh succeeded.
/// </summary>
public sealed class CatalogProvider : ICatalogProvider
{
    private readonly IUexApiClient _api;
    private readonly ILogger<CatalogProvider> _logger;
    private readonly string _cacheDir;
    private readonly TimeSpan _maxAge;

    private readonly Dictionary<int, UexCommodity> _commoditiesById = new();
    private readonly Dictionary<int, UexTerminal> _terminalsById = new();
    private readonly object _gate = new();

    private HashSet<string> _ambiguousNames = new(StringComparer.OrdinalIgnoreCase);

    public CatalogProvider(IUexApiClient api, ILogger<CatalogProvider> logger,
        string? cacheDir = null, TimeSpan? maxAge = null)
    {
        _api = api;
        _logger = logger;
        _cacheDir = cacheDir ?? DefaultCacheDir();
        _maxAge = maxAge ?? TimeSpan.FromHours(24);
        Directory.CreateDirectory(_cacheDir);
        TryLoadFromDisk();
    }

    public DateTimeOffset? LastRefreshedAt { get; private set; }
    public IReadOnlyList<UexCommodity> Commodities { get; private set; } = Array.Empty<UexCommodity>();
    public IReadOnlyList<UexTerminal> CommodityTerminals { get; private set; } = Array.Empty<UexTerminal>();

    public IReadOnlySet<string> AmbiguousTerminalNames
    {
        get { lock (_gate) return _ambiguousNames; }
    }

    public bool IsAmbiguous(UexTerminal terminal)
    {
        if (terminal is null) return false;
        var key = !string.IsNullOrWhiteSpace(terminal.DisplayName) ? terminal.DisplayName : terminal.Name;
        if (string.IsNullOrWhiteSpace(key)) return false;
        lock (_gate) return _ambiguousNames.Contains(key);
    }

    public event EventHandler? Refreshed;

    public UexCommodity? GetCommodity(int id)
    {
        lock (_gate) return _commoditiesById.GetValueOrDefault(id);
    }

    public UexTerminal? GetTerminal(int id)
    {
        lock (_gate) return _terminalsById.GetValueOrDefault(id);
    }

    /// <summary>
    /// Recomputes the set of terminal display names that exist in 2+ star systems.
    /// Called after every catalog load (disk OR API) so the lookup is always
    /// consistent with the in-memory <see cref="CommodityTerminals"/> list.
    /// MUST be called while holding <see cref="_gate"/>.
    /// </summary>
    private void RebuildAmbiguousNamesNoLock()
    {
        var byName = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in CommodityTerminals)
        {
            var key = !string.IsNullOrWhiteSpace(t.DisplayName) ? t.DisplayName : t.Name;
            if (string.IsNullOrWhiteSpace(key)) continue;
            var sys = t.StarSystemName ?? "";
            if (!byName.TryGetValue(key, out var systems))
            {
                systems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                byName[key] = systems;
            }
            systems.Add(sys);
        }
        _ambiguousNames = byName
            .Where(kv => kv.Value.Count >= 2)
            .Select(kv => kv.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public async Task<bool> RefreshAsync(bool force = false, CancellationToken ct = default)
    {
        if (!force && LastRefreshedAt is { } at && (DateTimeOffset.UtcNow - at) < _maxAge && Commodities.Count > 0)
        {
            _logger.LogDebug("Catalog still fresh ({Age}); skip refresh.", DateTimeOffset.UtcNow - at);
            return false;
        }

        _logger.LogInformation("Refreshing UEX catalog from API...");

        var commodities = await _api.GetCommoditiesAsync(ct).ConfigureAwait(false);
        var terminals = await _api.GetCommodityTerminalsAsync(ct).ConfigureAwait(false);

        lock (_gate)
        {
            Commodities = commodities;
            CommodityTerminals = terminals;
            _commoditiesById.Clear();
            _terminalsById.Clear();
            foreach (var c in commodities) _commoditiesById[c.Id] = c;
            foreach (var t in terminals) _terminalsById[t.Id] = t;
            LastRefreshedAt = DateTimeOffset.UtcNow;
            RebuildAmbiguousNamesNoLock();
        }

        await SaveToDiskAsync(commodities, terminals, ct).ConfigureAwait(false);
        Refreshed?.Invoke(this, EventArgs.Empty);
        _logger.LogInformation("UEX catalog refreshed: {C} commodities, {T} terminals.",
            commodities.Count, terminals.Count);
        return true;
    }

    private void TryLoadFromDisk()
    {
        try
        {
            var commPath = Path.Combine(_cacheDir, "commodities.json");
            var termPath = Path.Combine(_cacheDir, "terminals.json");
            var metaPath = Path.Combine(_cacheDir, "_metadata.json");

            if (!File.Exists(commPath) || !File.Exists(termPath)) return;

            var commJson = File.ReadAllText(commPath);
            var termJson = File.ReadAllText(termPath);

            var commEnv = JsonSerializer.Deserialize<UexEnvelope<UexCommodity>>(commJson, Json);
            var termEnv = JsonSerializer.Deserialize<UexEnvelope<UexTerminal>>(termJson, Json);

            if (commEnv is null || termEnv is null) return;

            // Schema check: legacy caches (pre-scope-aware Trade Routes) didn't
            // serialize id_orbit / id_planet / id_star_system. If the entire
            // terminals list has every hierarchy ID at 0, the cache predates
            // the new schema — drop it so the next refresh repopulates.
            if (termEnv.Data.Count > 0 && termEnv.Data.All(t =>
                t.IdOrbit == 0 && t.IdPlanet == 0 && t.IdStarSystem == 0))
            {
                _logger.LogInformation(
                    "UEX catalog cache predates orbit/planet schema; discarding to force refresh.");
                return;
            }

            lock (_gate)
            {
                Commodities = commEnv.Data;
                CommodityTerminals = termEnv.Data;
                _commoditiesById.Clear();
                _terminalsById.Clear();
                foreach (var c in commEnv.Data) _commoditiesById[c.Id] = c;
                foreach (var t in termEnv.Data) _terminalsById[t.Id] = t;
                RebuildAmbiguousNamesNoLock();
            }

            if (File.Exists(metaPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(metaPath));
                if (doc.RootElement.TryGetProperty("downloaded_at", out var at)
                    && DateTimeOffset.TryParse(at.GetString(), out var ts))
                {
                    LastRefreshedAt = ts;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load UEX catalog cache from disk; ignoring.");
        }
    }

    private async Task SaveToDiskAsync(IReadOnlyList<UexCommodity> c, IReadOnlyList<UexTerminal> t, CancellationToken ct)
    {
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(_cacheDir, "commodities.json"),
                JsonSerializer.Serialize(new UexEnvelope<UexCommodity> { Status = "ok", Data = c.ToList() }, Json),
                ct);
            await File.WriteAllTextAsync(
                Path.Combine(_cacheDir, "terminals.json"),
                JsonSerializer.Serialize(new UexEnvelope<UexTerminal> { Status = "ok", Data = t.ToList() }, Json),
                ct);
            await File.WriteAllTextAsync(
                Path.Combine(_cacheDir, "_metadata.json"),
                JsonSerializer.Serialize(new
                {
                    downloaded_at = DateTimeOffset.UtcNow.ToString("O"),
                    commodity_count = c.Count,
                    terminal_count = t.Count,
                }, Json),
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist UEX catalog cache to disk; in-memory only.");
        }
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
    };

    private static string DefaultCacheDir()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SC-DataRunnerNet",
            "uex_cache");
}
