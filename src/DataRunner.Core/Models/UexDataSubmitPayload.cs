using System.Text.Json.Serialization;

namespace DataRunner.Core.Models;

/// <summary>
/// Exact wire format for POST https://api.uexcorp.space/2.0/data_submit
/// Reference: https://uexcorp.space/api/documentation/id/post_data_submit/
///
/// IMPORTANT: the `_meta` block is a NON-UEX local extension used by this app
/// for review/validation. It MUST be stripped before serialising to the wire
/// (UexApiClient handles this via a dedicated serializer).
/// </summary>
public sealed class UexDataSubmitPayload
{
    [JsonPropertyName("id_terminal")] public int IdTerminal { get; set; }
    [JsonPropertyName("type")] public string Type { get; set; } = "commodity";
    [JsonPropertyName("is_production")] public int IsProduction { get; set; } = 0;

    [JsonPropertyName("prices")] public List<UexPriceRow> Prices { get; set; } = new();

    [JsonPropertyName("container_sizes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ContainerSizes { get; set; }

    [JsonPropertyName("game_version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GameVersion { get; set; }

    [JsonPropertyName("faction_affinity")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? FactionAffinity { get; set; }

    [JsonPropertyName("details")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Details { get; set; }

    [JsonPropertyName("screenshot")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Screenshot { get; set; }

    /// <summary>Local debugging metadata. NEVER sent to UEX.</summary>
    [JsonPropertyName("_meta")] public PayloadMeta Meta { get; set; } = new();
}

public sealed class UexPriceRow
{
    [JsonPropertyName("id_commodity")] public int IdCommodity { get; set; }

    [JsonPropertyName("price_buy")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? PriceBuy { get; set; }

    [JsonPropertyName("scu_buy")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ScuBuy { get; set; }

    [JsonPropertyName("status_buy")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? StatusBuy { get; set; }

    [JsonPropertyName("price_sell")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? PriceSell { get; set; }

    [JsonPropertyName("scu_sell")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ScuSell { get; set; }

    [JsonPropertyName("status_sell")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? StatusSell { get; set; }

    [JsonPropertyName("is_missing")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? IsMissing { get; set; }
}

public sealed class PayloadMeta
{
    [JsonPropertyName("draft")] public bool Draft { get; set; } = true;
    [JsonPropertyName("source_image")] public string SourceImage { get; set; } = "";
    [JsonPropertyName("terminal_display_name")] public string? TerminalDisplayName { get; set; }
    [JsonPropertyName("terminal_match_score")] public double TerminalMatchScore { get; set; }
    [JsonPropertyName("terminal_matched_from_ocr")] public string? TerminalMatchedFromOcr { get; set; }
    [JsonPropertyName("terminal_matched_field")] public string? TerminalMatchedField { get; set; }
    [JsonPropertyName("tab_detected")] public string TabDetected { get; set; } = "unknown";
    [JsonPropertyName("commodity_match_scores")] public List<int> CommodityMatchScores { get; set; } = new();
    [JsonPropertyName("needs_review")] public List<string> NeedsReview { get; set; } = new();
    [JsonPropertyName("warnings")] public List<string> Warnings { get; set; } = new();
}
