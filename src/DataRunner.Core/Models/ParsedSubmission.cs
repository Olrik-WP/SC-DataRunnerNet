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
