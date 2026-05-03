using System.Text.Json.Serialization;

namespace DataRunner.Core.Models;

/// <summary>
/// One row from <c>GET /commodities_prices_all</c>.
///
/// This endpoint returns the full universe of currently-tracked commodity prices
/// (already filtered by UEX to "active" rows: at least one of price_buy / price_sell
/// is non-zero). One single API call yields the entire dataset (~1 MB), so it is
/// the preferred source for the "Stale Targets" panel — caching it locally for
/// 6h gives us a near-zero impact on the UEX quota (4 calls/day).
///
/// The DTO keeps only the fields we use; UEX may add more without breaking us
/// because we use property-name binding (snake_case via <see cref="JsonPropertyNameAttribute"/>).
/// </summary>
public sealed class UexCommodityPriceAll
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("id_commodity")] public int IdCommodity { get; set; }
    [JsonPropertyName("id_terminal")] public int IdTerminal { get; set; }

    [JsonPropertyName("price_buy")] public double PriceBuy { get; set; }
    [JsonPropertyName("price_sell")] public double PriceSell { get; set; }

    [JsonPropertyName("scu_buy")] public double ScuBuy { get; set; }
    [JsonPropertyName("scu_sell_stock")] public double ScuSellStock { get; set; }

    [JsonPropertyName("status_buy")] public int StatusBuy { get; set; }
    [JsonPropertyName("status_sell")] public int StatusSell { get; set; }

    [JsonPropertyName("date_added")] public long DateAdded { get; set; }

    /// <summary>UNIX epoch (seconds) of the last successful data_submit affecting this row.</summary>
    [JsonPropertyName("date_modified")] public long DateModified { get; set; }

    [JsonPropertyName("commodity_name")] public string CommodityName { get; set; } = "";
    [JsonPropertyName("terminal_name")] public string TerminalName { get; set; } = "";
}
