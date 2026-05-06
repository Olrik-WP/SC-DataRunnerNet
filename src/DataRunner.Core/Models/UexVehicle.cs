using System.Text.Json.Serialization;

namespace DataRunner.Core.Models;

/// <summary>
/// One row from <c>GET /vehicles</c>. Used by <c>IVehicleCatalog</c> to feed the
/// vehicle-selection combo on the Trade Routes view.
///
/// We only care about cargo-capable ships (filter <c>IsCargo == 1 &amp;&amp; Scu &gt; 0</c>
/// at the catalog level) but we keep the broader feature flags around so the
/// view can render badges (ground vehicle, refuel, etc.) without re-fetching.
///
/// Server cache TTL: +12h. Update frequency: per patch cycle. Local cache: 24h.
/// </summary>
public sealed class UexVehicle
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("id_company")] public int IdCompany { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("name_full")] public string NameFull { get; set; } = "";
    [JsonPropertyName("slug")] public string Slug { get; set; } = "";

    /// <summary>Cargo capacity in SCU.</summary>
    [JsonPropertyName("scu")] public double Scu { get; set; }

    /// <summary>CSV of supported container sizes (e.g. <c>"1,2,4,8,16,24,32"</c>).</summary>
    [JsonPropertyName("container_sizes")] public string? ContainerSizes { get; set; }

    [JsonPropertyName("is_cargo")] public int IsCargo { get; set; }
    [JsonPropertyName("is_spaceship")] public int IsSpaceship { get; set; }
    [JsonPropertyName("is_ground_vehicle")] public int IsGroundVehicle { get; set; }
    [JsonPropertyName("is_loading_dock")] public int IsLoadingDock { get; set; }
    [JsonPropertyName("is_concept")] public int IsConcept { get; set; }

    /// <summary>One of XS / S / M / L / XL — landing pad size required.</summary>
    [JsonPropertyName("pad_type")] public string? PadType { get; set; }

    [JsonPropertyName("game_version")] public string? GameVersion { get; set; }

    [JsonPropertyName("company_name")] public string? CompanyName { get; set; }

    /// <summary>
    /// Human-readable label used by the combo, of the form
    /// <c>"Drake Caterpillar (576 SCU)"</c>. Falls back to <see cref="Name"/>
    /// when company / SCU are missing.
    /// </summary>
    public string DisplayLabel
    {
        get
        {
            var bestName = !string.IsNullOrWhiteSpace(NameFull) ? NameFull : Name;
            var prefix = string.IsNullOrWhiteSpace(CompanyName) ? bestName : $"{CompanyName} {bestName}";
            return Scu > 0
                ? $"{prefix} ({Scu:0} SCU)"
                : prefix;
        }
    }

    /// <summary>
    /// Parses <see cref="ContainerSizes"/> CSV into a stable, sorted, distinct
    /// integer set. Returns an empty set when the field is null/empty so the
    /// caller can treat "no constraint declared" as "any container size accepted".
    /// </summary>
    public IReadOnlySet<int> ParsedContainerSizes()
    {
        if (string.IsNullOrWhiteSpace(ContainerSizes)) return new HashSet<int>();
        var set = new HashSet<int>();
        foreach (var token in ContainerSizes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (int.TryParse(token, out var n) && n > 0) set.Add(n);
        }
        return set;
    }
}
