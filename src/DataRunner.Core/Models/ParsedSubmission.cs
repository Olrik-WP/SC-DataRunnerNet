namespace DataRunner.Core.Models;

/// <summary>
/// Internal pipeline output: OCR + parsing + matching.
/// Convert via UexPayloadBuilder to the wire-format UexDataSubmitPayload.
/// </summary>
public sealed class ParsedSubmission
{
    public string SourceImage { get; set; } = "";
    public string Type { get; set; } = "commodity";
    public int IsProduction { get; set; } = 0;
    public TerminalTab Tab { get; set; } = TerminalTab.Unknown;

    /// <summary>
    /// Star Citizen game build the screenshot was taken from. Determined by
    /// the watcher slot that picked up the file (LIVE folder vs PTU folder),
    /// not by parsing the file path. Resolved at submission time to the
    /// actual UEX-recognised build number via /game_versions and used as
    /// the <c>game_version</c> field of the /data_submit payload.
    /// </summary>
    public GameBranch Branch { get; set; } = GameBranch.Live;

    /// <summary>
    /// Width of the source screenshot in pixels (0 when not set, eg. test
    /// submissions built from raw text). Surfaced to the validation UI so
    /// it can warn the user when the image was clearly resized / cropped
    /// to a non-standard aspect ratio (which degrades the small-glyph OCR
    /// reliability — most importantly the terminal name in the LEFT panel).
    /// </summary>
    public int SourceImageWidth { get; set; }

    /// <summary>Height of the source screenshot in pixels. See
    /// <see cref="SourceImageWidth"/>.</summary>
    public int SourceImageHeight { get; set; }

    public int? IdTerminal { get; set; }
    public string? TerminalDisplayName { get; set; }
    public double TerminalMatchScore { get; set; }
    public string? TerminalMatchedFromOcr { get; set; }
    public string? TerminalMatchedField { get; set; }

    public string? ContainerSizes { get; set; }
    public List<ParsedPriceRow> Prices { get; set; } = new();

    public List<string> NeedsReview { get; set; } = new();
    public string? Notes { get; set; }
}

public sealed class ParsedPriceRow
{
    public int? IdCommodity { get; set; }
    public string? CommodityName { get; set; }
    public string? CommodityCode { get; set; }
    public double CommodityMatchScore { get; set; }
    public string? CommodityMatchedFromOcr { get; set; }

    public double? PriceBuy { get; set; }
    public int? ScuBuy { get; set; }
    public InventoryStatus StatusBuy { get; set; } = InventoryStatus.Unknown;

    public string? RawScu { get; set; }
    public string? RawPrice { get; set; }
    public string? RawStatus { get; set; }
}
