namespace DataRunner.OcrSpike.Models;

public sealed class GroundTruth
{
    public string Image { get; set; } = "";
    public string Terminal { get; set; } = "";
    public string TerminalType { get; set; } = "";
    public List<GroundTruthRow> Rows { get; set; } = new();
    public string ExpectedText { get; set; } = "";
}

public sealed class GroundTruthRow
{
    public string Commodity { get; set; } = "";
    public string Quantity { get; set; } = "";
    public string Price { get; set; } = "";
}
