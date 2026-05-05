using System.Text.Json.Serialization;

namespace DataRunner.Core.Models;

/// <summary>UEX terminal catalog entry (subset of fields used by this app).</summary>
public sealed class UexTerminal
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("displayname")] public string DisplayName { get; set; } = "";
    [JsonPropertyName("nickname")] public string Nickname { get; set; } = "";
    [JsonPropertyName("code")] public string Code { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("star_system_name")] public string? StarSystemName { get; set; }
    [JsonPropertyName("planet_name")] public string? PlanetName { get; set; }
    [JsonPropertyName("space_station_name")] public string? SpaceStationName { get; set; }
    [JsonPropertyName("outpost_name")] public string? OutpostName { get; set; }
    [JsonPropertyName("city_name")] public string? CityName { get; set; }
    [JsonPropertyName("is_visible")] public int IsVisible { get; set; }
    [JsonPropertyName("is_available_live")] public int IsAvailableLive { get; set; }

    public string LocationLabel
    {
        get
        {
            var parts = new[] { CityName, OutpostName, SpaceStationName, PlanetName, StarSystemName }
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToArray();
            return parts.Length > 0 ? string.Join(" / ", parts!) : "";
        }
    }

    /// <summary>
    /// Hierarchical, human-readable terminal label of the form
    /// <c>"Shop · Parent location · Star system"</c>, e.g.
    /// <c>"Platinum Bay · Nyx Gateway · Stanton"</c>.
    /// <para>
    /// UEX commonly sets <see cref="DisplayName"/> to the parent station name
    /// (e.g. <c>"Nyx Gateway"</c>), which means several sibling shops at the
    /// same station collapse to the same display label. We surface the more
    /// specific <see cref="Name"/> first whenever it differs, so the dropdown
    /// can disambiguate <c>Platinum Bay</c> from <c>Cargo Deck</c> at a glance.
    /// The trailing star system disambiguates Stanton vs Pyro.
    /// </para>
    /// <para>
    /// Falls back gracefully when fields are missing (legacy data).
    /// </para>
    /// </summary>
    public string RichDisplayName
    {
        get
        {
            var primary = ResolvePrimaryLabel();
            var parent = ResolveParentLabel();
            var system = (StarSystemName ?? "").Trim();

            var parts = new List<string>(3);
            if (!string.IsNullOrWhiteSpace(primary)) parts.Add(primary);
            if (!string.IsNullOrWhiteSpace(parent)
                && !string.Equals(parent, primary, StringComparison.OrdinalIgnoreCase))
            {
                parts.Add(parent);
            }
            if (!string.IsNullOrWhiteSpace(system)
                && !string.Equals(system, parent, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(system, primary, StringComparison.OrdinalIgnoreCase))
            {
                parts.Add(system);
            }
            return string.Join(" · ", parts);
        }
    }

    /// <summary>
    /// Most-specific identifying label for this terminal: prefers <see cref="Name"/>
    /// (often a shop name like <c>"Platinum Bay"</c>) when it differs from the
    /// parent-station <see cref="DisplayName"/>; falls back to <c>DisplayName</c>
    /// otherwise. Used by <see cref="RichDisplayName"/> and as the search
    /// "leaf" token for fuzzy/exact matching.
    /// </summary>
    private string ResolvePrimaryLabel()
    {
        var name = (Name ?? "").Trim();
        var displayName = (DisplayName ?? "").Trim();
        if (string.IsNullOrEmpty(name)) return displayName;
        if (string.IsNullOrEmpty(displayName)) return name;
        return string.Equals(name, displayName, StringComparison.OrdinalIgnoreCase)
            ? displayName
            : name;
    }

    /// <summary>
    /// Best parent-location label, walking from most specific (city / outpost /
    /// station) to less specific (planet). Strips the UEX "(System)" suffix
    /// from <c>SpaceStationName</c> values like <c>"Nyx Gateway (Stanton)"</c>
    /// so we don't print the system twice.
    /// </summary>
    private string ResolveParentLabel()
    {
        foreach (var candidate in new[] { CityName, OutpostName, SpaceStationName, PlanetName })
        {
            if (!string.IsNullOrWhiteSpace(candidate)) return StripSystemSuffix(candidate);
        }
        return "";
    }

    private static string StripSystemSuffix(string s)
    {
        var idx = s.IndexOf(" (", StringComparison.Ordinal);
        return idx > 0 ? s[..idx].Trim() : s.Trim();
    }
}
