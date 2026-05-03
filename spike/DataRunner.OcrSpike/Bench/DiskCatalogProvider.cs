using System.Text.Json;
using DataRunner.Core.Abstractions;
using DataRunner.Core.Models;

namespace DataRunner.OcrSpike.Bench;

/// <summary>
/// Minimal <see cref="ICatalogProvider"/> for the offline OCR benchmark spike.
/// Loads commodities/terminals from local JSON cache (samples/uex_cache) ONLY;
/// never makes any HTTP call. Use the production CatalogProvider in DataRunner.UexClient
/// when you need live refreshes from the UEX API.
/// </summary>
public sealed class DiskCatalogProvider : ICatalogProvider
{
    private readonly Dictionary<int, UexCommodity> _commoditiesById;
    private readonly Dictionary<int, UexTerminal> _terminalsById;

    public IReadOnlyList<UexCommodity> Commodities { get; }
    public IReadOnlyList<UexTerminal> CommodityTerminals { get; }
    public DateTimeOffset? LastRefreshedAt { get; }

    public event EventHandler? Refreshed { add { } remove { } }

    private DiskCatalogProvider(
        IReadOnlyList<UexCommodity> commodities,
        IReadOnlyList<UexTerminal> terminals,
        DateTimeOffset? lastRefreshedAt)
    {
        Commodities = commodities;
        CommodityTerminals = terminals;
        LastRefreshedAt = lastRefreshedAt;
        _commoditiesById = commodities.ToDictionary(c => c.Id);
        _terminalsById = terminals.ToDictionary(t => t.Id);
    }

    public static DiskCatalogProvider LoadFromCache(string cacheDir)
    {
        var commPath = Path.Combine(cacheDir, "commodities.json");
        var termPath = Path.Combine(cacheDir, "terminals.json");

        if (!File.Exists(commPath) || !File.Exists(termPath))
            throw new FileNotFoundException(
                $"UEX cache missing. Expected {commPath} and {termPath}.");

        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        var commEnv = JsonSerializer.Deserialize<UexEnvelope<UexCommodity>>(
            File.ReadAllText(commPath), opts)
            ?? throw new InvalidOperationException("Failed to parse commodities.json");

        var termEnv = JsonSerializer.Deserialize<UexEnvelope<UexTerminal>>(
            File.ReadAllText(termPath), opts)
            ?? throw new InvalidOperationException("Failed to parse terminals.json");

        var visibleCommodities = commEnv.Data.Where(c => c.IsVisible == 1).ToList();
        var commodityTerminals = termEnv.Data
            .Where(t => string.Equals(t.Type, "commodity", StringComparison.OrdinalIgnoreCase)
                     && t.IsVisible == 1)
            .ToList();

        DateTimeOffset? lastRefreshed = null;
        var metaPath = Path.Combine(cacheDir, "_metadata.json");
        if (File.Exists(metaPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(metaPath));
                if (doc.RootElement.TryGetProperty("downloaded_at", out var at)
                    && DateTimeOffset.TryParse(at.GetString(), out var ts))
                {
                    lastRefreshed = ts;
                }
            }
            catch { /* ignore */ }
        }

        return new DiskCatalogProvider(visibleCommodities, commodityTerminals, lastRefreshed);
    }

    public UexCommodity? GetCommodity(int id) => _commoditiesById.GetValueOrDefault(id);
    public UexTerminal? GetTerminal(int id) => _terminalsById.GetValueOrDefault(id);

    public Task<bool> RefreshAsync(bool force = false, CancellationToken ct = default)
        => Task.FromResult(false);
}
