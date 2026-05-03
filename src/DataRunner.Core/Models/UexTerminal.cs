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
    /// Compact label for UI: <c>"Display Name · Star System"</c>. Always includes
    /// the star system so the user can disambiguate Pyro Gateway (Stanton) from
    /// Pyro Gateway (Pyro), per UEX community feedback. Falls back gracefully
    /// when StarSystemName is missing (legacy data).
    /// </summary>
    public string RichDisplayName
    {
        get
        {
            var name = !string.IsNullOrWhiteSpace(DisplayName) ? DisplayName : Name;
            return string.IsNullOrWhiteSpace(StarSystemName)
                ? name
                : $"{name} · {StarSystemName}";
        }
    }
}
