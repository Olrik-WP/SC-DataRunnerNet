using System.Text.Json.Serialization;

namespace DataRunner.Core.Models;

/// <summary>UEX commodity catalog entry (subset of fields used by this app).</summary>
public sealed class UexCommodity
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("code")] public string Code { get; set; } = "";
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("is_visible")] public int IsVisible { get; set; }
    [JsonPropertyName("is_buyable")] public int IsBuyable { get; set; }
    [JsonPropertyName("is_sellable")] public int IsSellable { get; set; }
    [JsonPropertyName("is_available_live")] public int IsAvailableLive { get; set; }

    /// <summary>
    /// 1 = contraband / illegal commodity (e.g. Altruciatoxin, WiDoW). Powers
    /// the "Legal" filter on the Trade Routes view: when on, every route
    /// trading an illegal commodity is hidden.
    /// </summary>
    [JsonPropertyName("is_illegal")] public int IsIllegal { get; set; }
}

/// <summary>Standard UEX response envelope: { "status": "ok", "data": [...] }.</summary>
public sealed class UexEnvelope<T>
{
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    [JsonPropertyName("data")] public List<T> Data { get; set; } = new();
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("http_code")] public int? HttpCode { get; set; }
}
