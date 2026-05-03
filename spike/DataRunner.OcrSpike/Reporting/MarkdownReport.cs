using System.Globalization;
using System.Text;
using System.Text.Json;
using DataRunner.Core.Abstractions;
using DataRunner.Core.Models;
using DataRunner.OcrSpike.Models;

namespace DataRunner.OcrSpike.Reporting;

public static class MarkdownReport
{
    public static string Build(
        IReadOnlyList<BenchmarkRow> rows,
        IReadOnlyList<(string Image, OcrPipelineResult Result)>? pipelineRuns = null)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();

        sb.AppendLine("# OCR Spike Benchmark");
        sb.AppendLine();
        sb.AppendLine($"_Generated at {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC_");
        sb.AppendLine();

        sb.AppendLine("> **DRAFT - DO NOT SUBMIT.** All payloads below are produced by an automated pipeline,");
        sb.AppendLine("> may contain OCR errors, and MUST be reviewed by a human operator before being POSTed");
        sb.AppendLine("> to UEX. The spike runs with `is_production = 0` to avoid polluting live data.");
        sb.AppendLine();

        sb.AppendLine("## Per-image OCR scores (CER/WER)");
        sb.AppendLine();
        sb.AppendLine("| Image | Engine | CER | WER | Mean Conf. | Time (ms) |");
        sb.AppendLine("|-------|--------|-----|-----|------------|-----------|");
        foreach (var r in rows)
        {
            sb.AppendLine(string.Format(inv,
                "| {0} | {1} | {2:F3} | {3:F3} | {4:F2} | {5} |",
                r.Image, r.Engine, r.Cer, r.Wer, r.MeanConfidence, r.ElapsedMs));
        }

        sb.AppendLine();
        sb.AppendLine("## Aggregate (mean per engine)");
        sb.AppendLine();
        sb.AppendLine("| Engine | Mean CER | Mean WER | Mean Conf. | Mean Time (ms) |");
        sb.AppendLine("|--------|----------|----------|------------|----------------|");
        foreach (var grp in rows.GroupBy(x => x.Engine))
        {
            sb.AppendLine(string.Format(inv,
                "| {0} | {1:F3} | {2:F3} | {3:F2} | {4:F0} |",
                grp.Key,
                grp.Average(x => x.Cer),
                grp.Average(x => x.Wer),
                grp.Average(x => x.MeanConfidence),
                grp.Average(x => x.ElapsedMs)));
        }

        if (pipelineRuns is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("## Pipeline structured output (the real metric)");
            sb.AppendLine();
            sb.AppendLine("Each row = one screenshot.");
            sb.AppendLine("- **Terminal matched** = could the right Star Citizen kiosk be resolved against the UEX catalog?");
            sb.AppendLine("- **Tab** = BUY (shop sells to player) vs SELL (shop buys from player, \"local market value\")");
            sb.AppendLine("- **Rows parsed** = how many commodity rows were extracted");
            sb.AppendLine("- **Rows complete** = rows with id_commodity + SCU + price all set (= submittable to UEX)");
            sb.AppendLine();
            sb.AppendLine("| Image | Terminal (id, score) | Tab | Container sizes | Rows parsed | Rows complete | Mean commodity score |");
            sb.AppendLine("|-------|----------------------|-----|-----------------|-------------|---------------|----------------------|");

            foreach (var (image, run) in pipelineRuns)
            {
                var s = run.Submission;
                var terminalCell = s.IdTerminal is null
                    ? "_not detected_"
                    : $"{s.TerminalDisplayName} ({s.IdTerminal}, score {s.TerminalMatchScore:F0})";
                var rowsParsed = s.Prices.Count;
                var rowsComplete = s.Prices.Count(p => p.IdCommodity is not null
                                                      && p.ScuBuy is not null
                                                      && p.PriceBuy is not null);
                var meanCommScore = s.Prices.Count > 0
                    ? s.Prices.Average(p => p.CommodityMatchScore)
                    : 0.0;

                sb.AppendLine(string.Format(inv,
                    "| {0} | {1} | {2} | {3} | {4} | {5} | {6:F0} |",
                    image,
                    terminalCell,
                    s.Tab,
                    s.ContainerSizes ?? "-",
                    rowsParsed,
                    rowsComplete,
                    meanCommScore));
            }

            sb.AppendLine();
            sb.AppendLine("### Per-screenshot detail");
            sb.AppendLine();

            foreach (var (image, run) in pipelineRuns)
            {
                var s = run.Submission;
                sb.AppendLine($"#### {image}");
                sb.AppendLine();

                if (s.IdTerminal is not null)
                {
                    sb.AppendLine($"**Terminal**: `{s.TerminalDisplayName}` "
                        + $"(id={s.IdTerminal}, score={s.TerminalMatchScore:F0}, "
                        + $"matched on `{s.TerminalMatchedField}` from OCR `\"{s.TerminalMatchedFromOcr}\"`)");
                }
                else
                {
                    sb.AppendLine("**Terminal**: not detected");
                }

                sb.AppendLine();
                sb.AppendLine($"**Tab**: `{s.Tab}` &nbsp;&nbsp; **Container sizes (SCU)**: `{s.ContainerSizes ?? "-"}`");
                sb.AppendLine();

                sb.AppendLine("| # | Commodity (matched) | Score | Code | SCU | Price | Status | OCR commodity | OCR SCU | OCR price | OCR status |");
                sb.AppendLine("|---|---------------------|-------|------|-----|-------|--------|---------------|---------|-----------|------------|");

                for (var i = 0; i < s.Prices.Count; i++)
                {
                    var p = s.Prices[i];
                    sb.AppendLine(string.Format(inv,
                        "| {0} | {1} | {2:F0} | {3} | {4} | {5} | {6} | {7} | {8} | {9} | {10} |",
                        i + 1,
                        p.CommodityName ?? "_?_",
                        p.CommodityMatchScore,
                        p.CommodityCode ?? "-",
                        p.ScuBuy?.ToString(inv) ?? "_?_",
                        p.PriceBuy?.ToString("F2", inv) ?? "_?_",
                        p.StatusBuy == InventoryStatus.Unknown ? "?" : $"{(int)p.StatusBuy}({p.StatusBuy})",
                        Md(p.CommodityMatchedFromOcr),
                        Md(p.RawScu),
                        Md(p.RawPrice),
                        Md(p.RawStatus)));
                }

                sb.AppendLine();
                if (s.NeedsReview.Count > 0)
                {
                    sb.AppendLine("**Needs review**:");
                    foreach (var nr in s.NeedsReview)
                    {
                        sb.AppendLine($"- {nr}");
                    }
                    sb.AppendLine();
                }

                sb.AppendLine("**UEX payload preview** (DRAFT, _meta is local-only and is stripped before submission):");
                sb.AppendLine();
                sb.AppendLine("```json");
                sb.AppendLine(JsonSerializer.Serialize(run.Payload, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
                }));
                sb.AppendLine("```");
                sb.AppendLine();
            }
        }

        sb.AppendLine();
        sb.AppendLine("## Raw OCR output (per engine, per image)");
        sb.AppendLine();

        foreach (var r in rows)
        {
            sb.AppendLine($"### {r.Image} - {r.Engine}");
            sb.AppendLine();
            sb.AppendLine("```text");
            sb.AppendLine(r.RawText);
            sb.AppendLine("```");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string Md(string? s)
        => string.IsNullOrEmpty(s) ? "-" : "`" + s.Replace("|", "\\|").Replace("\n", " ") + "`";
}
