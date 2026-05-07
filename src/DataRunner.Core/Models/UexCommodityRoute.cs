using System.Text.Json.Serialization;

namespace DataRunner.Core.Models;

/// <summary>
/// One row from <c>GET /commodities_routes</c>.
///
/// UEX's pre-calculated trade routes: their algorithm crunches recent reports
/// on every (origin × destination × commodity) triplet and returns the top 500
/// rows ranked by their internal <see cref="Score"/>. We do NOT recompute any
/// of the trade-relevant fields (profit / ROI / score / volatility / distance /
/// container_sizes); we only consume them as-is.
///
/// We deliberately bind only the subset of fields we display or filter on —
/// UEX may add more without breaking us because we rely on JSON property-name
/// binding via <see cref="JsonPropertyNameAttribute"/>. The full schema (~70
/// fields) includes location-attribute flags (has_docking_port_*, has_loading_dock_*,
/// is_monitored_*, is_on_ground_*, etc.) which we surface as glyphs in the
/// row tooltip but most of them are pulled into named properties below.
///
/// Update frequency: hourly server-side. Server cache TTL: +30 minutes.
/// </summary>
public sealed class UexCommodityRoute
{
    [JsonPropertyName("id")] public long Id { get; set; }

    [JsonPropertyName("id_commodity")] public int IdCommodity { get; set; }

    [JsonPropertyName("id_terminal_origin")] public int IdTerminalOrigin { get; set; }
    [JsonPropertyName("id_terminal_destination")] public int IdTerminalDestination { get; set; }

    [JsonPropertyName("id_planet_origin")] public int IdPlanetOrigin { get; set; }
    [JsonPropertyName("id_planet_destination")] public int IdPlanetDestination { get; set; }

    [JsonPropertyName("id_star_system_origin")] public int IdStarSystemOrigin { get; set; }
    [JsonPropertyName("id_star_system_destination")] public int IdStarSystemDestination { get; set; }

    /// <summary>Unique route hash. Used to deep-link into UEX (<c>https://uexcorp.space/trade/route?code={code}</c>).</summary>
    [JsonPropertyName("code")] public string Code { get; set; } = "";

    [JsonPropertyName("price_origin")] public double PriceOrigin { get; set; }
    [JsonPropertyName("price_destination")] public double PriceDestination { get; set; }

    /// <summary>
    /// Number of distinct user-submitted price reports backing
    /// <c>price_origin</c>. <c>0</c> means UEX has no recent user data for the
    /// buy side and the price is a predicted/inferred value rather than a
    /// confirmed report. Used by the "Predicted" filter to surface routes that
    /// are good datarunner targets (worth visiting to confirm/refresh prices).
    /// </summary>
    [JsonPropertyName("price_origin_users_rows")] public int PriceOriginUsersRows { get; set; }

    /// <summary>Same as <see cref="PriceOriginUsersRows"/> but for the sell-side price at destination.</summary>
    [JsonPropertyName("price_destination_users_rows")] public int PriceDestinationUsersRows { get; set; }

    /// <summary>
    /// Margin as a PERCENTAGE: <c>(price_destination - price_origin) / price_destination × 100</c>.
    /// e.g. UEX returns <c>49.11</c> for an item bought at 229 and sold at 450
    /// (= 49.11% margin on the sell price). NOT a per-SCU UEC amount; do NOT
    /// multiply by SCU to get profit — use <c>price_destination - price_origin</c>
    /// directly for UEC margin per SCU.
    /// </summary>
    [JsonPropertyName("price_margin")] public double PriceMargin { get; set; }

    /// <summary>
    /// ROI as a PERCENTAGE: <c>(price_destination - price_origin) / price_origin × 100</c>.
    /// e.g. UEX returns <c>96.51</c> for the route above (96.51% ROI). NOT a 0..1
    /// fraction — display with <c>{0:N0}%</c> or similar, NOT <c>{0:P0}</c>.
    /// </summary>
    [JsonPropertyName("price_roi")] public double PriceRoi { get; set; }

    [JsonPropertyName("scu_origin")] public double ScuOrigin { get; set; }
    [JsonPropertyName("scu_destination")] public double ScuDestination { get; set; }

    /// <summary>Inventory level at origin, 1 (Out of Stock) … 7 (Maximum). Higher is better for buy-side.</summary>
    [JsonPropertyName("status_origin")] public int? StatusOrigin { get; set; }

    /// <summary>Inventory level at destination, 1 … 7. LOWER is better for sell-side (less competition).</summary>
    [JsonPropertyName("status_destination")] public int? StatusDestination { get; set; }

    /// <summary>Coefficient of variation on origin price; higher = more volatile = riskier.</summary>
    [JsonPropertyName("volatility_origin")] public double VolatilityOrigin { get; set; }

    [JsonPropertyName("volatility_destination")] public double VolatilityDestination { get; set; }

    /// <summary>Maximum investment expected for the route (scu_buy_avg × price_buy_avg).</summary>
    [JsonPropertyName("investment")] public double Investment { get; set; }

    /// <summary>Maximum profit expected. UEX-computed.</summary>
    [JsonPropertyName("profit")] public double Profit { get; set; }

    /// <summary>Distance in Giga Meters (Gm).</summary>
    [JsonPropertyName("distance")] public double Distance { get; set; }

    /// <summary>UEX score level — higher is better. Internal qualitative ranking; treat as opaque.</summary>
    [JsonPropertyName("score")] public int Score { get; set; }

    /// <summary>CSV of allowed container sizes at origin (e.g. <c>"1,2,4,8,16,24,32"</c>).</summary>
    [JsonPropertyName("container_sizes_origin")] public string? ContainerSizesOrigin { get; set; }

    [JsonPropertyName("container_sizes_destination")] public string? ContainerSizesDestination { get; set; }

    [JsonPropertyName("game_version_origin")] public string? GameVersionOrigin { get; set; }
    [JsonPropertyName("game_version_destination")] public string? GameVersionDestination { get; set; }

    // Logistics flags (1 = present, 0 = absent). Surfaced as glyphs in the row tooltip.
    [JsonPropertyName("has_docking_port_origin")] public int HasDockingPortOrigin { get; set; }
    [JsonPropertyName("has_docking_port_destination")] public int HasDockingPortDestination { get; set; }
    [JsonPropertyName("has_freight_elevator_origin")] public int HasFreightElevatorOrigin { get; set; }
    [JsonPropertyName("has_freight_elevator_destination")] public int HasFreightElevatorDestination { get; set; }
    [JsonPropertyName("has_loading_dock_origin")] public int HasLoadingDockOrigin { get; set; }
    [JsonPropertyName("has_loading_dock_destination")] public int HasLoadingDockDestination { get; set; }

    /// <summary>1 = origin terminal has on-site refuelling. Powers the "Refuel" filter.</summary>
    [JsonPropertyName("has_refuel_origin")] public int HasRefuelOrigin { get; set; }

    /// <summary>1 = destination terminal has on-site refuelling. Powers the "Refuel" filter.</summary>
    [JsonPropertyName("has_refuel_destination")] public int HasRefuelDestination { get; set; }

    [JsonPropertyName("is_monitored_origin")] public int IsMonitoredOrigin { get; set; }
    [JsonPropertyName("is_monitored_destination")] public int IsMonitoredDestination { get; set; }
    [JsonPropertyName("is_on_ground_origin")] public int IsOnGroundOrigin { get; set; }
    [JsonPropertyName("is_on_ground_destination")] public int IsOnGroundDestination { get; set; }
    [JsonPropertyName("is_space_station_origin")] public int IsSpaceStationOrigin { get; set; }
    [JsonPropertyName("is_space_station_destination")] public int IsSpaceStationDestination { get; set; }

    // Display strings.
    [JsonPropertyName("commodity_name")] public string CommodityName { get; set; } = "";
    [JsonPropertyName("commodity_code")] public string? CommodityCode { get; set; }

    /// <summary>UEX URL slug for the commodity — used to deep-link into the
    /// commodity's locations_buying / locations_selling tab so the user lands
    /// on the right side of the trade automatically.</summary>
    [JsonPropertyName("commodity_slug")] public string? CommoditySlug { get; set; }

    [JsonPropertyName("origin_terminal_name")] public string? OriginTerminalName { get; set; }
    [JsonPropertyName("origin_terminal_code")] public string? OriginTerminalCode { get; set; }

    /// <summary>UEX URL slug for the origin terminal — used to deep-link into the website's terminal info page.</summary>
    [JsonPropertyName("origin_terminal_slug")] public string? OriginTerminalSlug { get; set; }
    [JsonPropertyName("origin_planet_name")] public string? OriginPlanetName { get; set; }
    [JsonPropertyName("origin_orbit_name")] public string? OriginOrbitName { get; set; }
    [JsonPropertyName("origin_star_system_name")] public string? OriginStarSystemName { get; set; }
    [JsonPropertyName("origin_faction_name")] public string? OriginFactionName { get; set; }

    [JsonPropertyName("destination_terminal_name")] public string? DestinationTerminalName { get; set; }
    [JsonPropertyName("destination_terminal_code")] public string? DestinationTerminalCode { get; set; }

    /// <summary>UEX URL slug for the destination terminal — used to deep-link into the website's terminal info page.</summary>
    [JsonPropertyName("destination_terminal_slug")] public string? DestinationTerminalSlug { get; set; }
    [JsonPropertyName("destination_planet_name")] public string? DestinationPlanetName { get; set; }
    [JsonPropertyName("destination_orbit_name")] public string? DestinationOrbitName { get; set; }
    [JsonPropertyName("destination_star_system_name")] public string? DestinationStarSystemName { get; set; }
    [JsonPropertyName("destination_faction_name")] public string? DestinationFactionName { get; set; }

    [JsonPropertyName("date_added")] public long DateAdded { get; set; }
}
