using System.Text.Json.Serialization;

namespace DataRunner.Core.Models;

/// <summary>
/// Response shape of GET /game_versions on the UEX 2.0 API. The endpoint
/// is the authoritative source for which Star Citizen build numbers are
/// currently accepted by /data_submit's <c>game_version</c> field.
///
/// Live example:
///   GET https://api.uexcorp.space/2.0/game_versions
///   { "status":"ok", "data": { "live":"4.7.2", "ptu":"4.8.0" }, "message":"" }
///
/// See https://uexcorp.space/api/documentation/id/get_game_versions/
/// </summary>
public sealed class UexGameVersions
{
    /// <summary>Current LIVE version string, e.g. "4.7.2". Null when UEX has none configured (rare).</summary>
    [JsonPropertyName("live")]
    public string? Live { get; set; }

    /// <summary>Current PTU version string, e.g. "4.8.0". Null when no PTU is currently published by CIG.</summary>
    [JsonPropertyName("ptu")]
    public string? Ptu { get; set; }
}
