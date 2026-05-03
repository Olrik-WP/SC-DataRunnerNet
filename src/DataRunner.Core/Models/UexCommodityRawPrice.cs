using System.Text.Json.Serialization;

namespace DataRunner.Core.Models;

/// <summary>
/// Subset of the response from GET /commodities_raw_prices used by the duplicate / sanity check.
/// </summary>
public sealed class UexCommodityRawPrice
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("id_commodity")] public int IdCommodity { get; set; }
    [JsonPropertyName("id_terminal")] public int IdTerminal { get; set; }
    [JsonPropertyName("commodity_name")] public string CommodityName { get; set; } = "";
    [JsonPropertyName("commodity_code")] public string CommodityCode { get; set; } = "";
    [JsonPropertyName("terminal_name")] public string TerminalName { get; set; } = "";

    [JsonPropertyName("price_buy")] public double? PriceBuy { get; set; }
    [JsonPropertyName("price_buy_avg")] public double? PriceBuyAvg { get; set; }
    [JsonPropertyName("price_buy_avg_week")] public double? PriceBuyAvgWeek { get; set; }
    [JsonPropertyName("price_buy_min")] public double? PriceBuyMin { get; set; }
    [JsonPropertyName("price_buy_max")] public double? PriceBuyMax { get; set; }

    [JsonPropertyName("price_sell")] public double? PriceSell { get; set; }
    [JsonPropertyName("price_sell_avg")] public double? PriceSellAvg { get; set; }
    [JsonPropertyName("price_sell_avg_week")] public double? PriceSellAvgWeek { get; set; }
    [JsonPropertyName("price_sell_min")] public double? PriceSellMin { get; set; }
    [JsonPropertyName("price_sell_max")] public double? PriceSellMax { get; set; }

    [JsonPropertyName("date_added")] public long? DateAdded { get; set; }
    [JsonPropertyName("date_modified")] public long? DateModified { get; set; }
}
