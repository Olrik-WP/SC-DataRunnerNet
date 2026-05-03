using System.Globalization;
using System.Text.RegularExpressions;
using DataRunner.Core.Models;
using DataRunner.Ocr.Matching;

namespace DataRunner.Ocr.Pipeline;

/// <summary>
/// Parses raw OCR text from a Star Citizen commodity terminal screenshot
/// into structured commodity rows resolved against the UEX catalog.
/// Operates on the RIGHT panel only (left panel = player inventory, never to submit).
/// </summary>
public sealed class CommodityParser
{
    private static readonly Regex ScuRegex = new(
        @"(?<num>\d[\d.,]*)\s*[5Ss3][Cc][UuOo0Yy]\b",
        RegexOptions.Compiled);

    private static readonly Regex PriceRegex = new(
        @"(?<num>\d[\d.,]+)\s*(?<unit>[KMkm])?\s*[\/\\1lI|]\s*[5Ss3][Cc][UuOo0]\b",
        RegexOptions.Compiled);

    private static readonly string[] UiStopWords =
    {
        "SHOP QUANTITY", "SHOP INVENTORY", "SHOP",
        "MAX INVENTORY", "MIN INVENTORY",
        "HIGH INVENTORY", "VERY HIGH INVENTORY",
        "MEDIUM INVENTORY", "LOW INVENTORY", "VERY LOW INVENTORY",
        "AVAILABLE CARGO SIZE", "CARGO CAPACITY", "CARGO SIZE",
        "CURRENT BALANCE", "LOCAL MARKET VALUE",
        "IN STOCK", "OUT OF STOCK",
        "IN DEMAND", "NO DEMAND", "CANNOT SELL",
        "YOUR INVENTORIES", "COMMODITIES",
        "BUY", "SELL", "SELECT SUB-CATEGORY", "QUALITY",
    };

    private static readonly (Regex Pattern, InventoryStatus Status)[] StatusPatterns =
    {
        (new Regex(@"\bMAXIMUM\s*INVENTORY\b|\bMAX\s*INVENTORY\b|\bFULL\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), InventoryStatus.Maximum),
        (new Regex(@"\bVERY\s*HIGH\s*INVENTORY\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), InventoryStatus.VeryHigh),
        (new Regex(@"\bHIGH\s*INVENTORY\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), InventoryStatus.High),
        (new Regex(@"\bMEDIUM\s*INVENTORY\b|\bMED\s*INVENTORY\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), InventoryStatus.Medium),
        (new Regex(@"\bVERY\s*LOW\s*INVENTORY\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), InventoryStatus.VeryLow),
        (new Regex(@"\bLOW\s*INVENTORY\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), InventoryStatus.Low),
        (new Regex(@"\bOUT\s*OF\s*STOCK\b|\bEMPTY\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), InventoryStatus.OutOfStock),
    };

    private static readonly int[] KnownContainerSizes = { 32, 24, 16, 8, 4, 2, 1 };

    private readonly FuzzyMatcher _matcher;
    private readonly int _commodityMinScore;

    public CommodityParser(FuzzyMatcher matcher, int commodityMinScore = 85)
    {
        _matcher = matcher;
        _commodityMinScore = commodityMinScore;
    }

    public ParsedSubmission Parse(string ocrText, string sourceImage)
    {
        var submission = new ParsedSubmission { SourceImage = sourceImage };

        var lines = ocrText.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToArray();

        submission.Tab = DetectTab(lines);
        submission.ContainerSizes = DetectContainerSizes(lines);

        var classified = ClassifyLines(lines);

        // NOTE: terminal detection is intentionally NOT done here.
        // The right panel only contains shop inventory (commodity names, prices, etc.)
        // and would falsely match "SCRAP" -> "Devlin Scrap & Salvage" displayname.
        // Terminal name detection is handled exclusively by PaddleOcrPipeline using
        // the top banner and left-panel header passes (which contain the station name).

        BuildPriceRows(classified, submission);
        DedupeRows(submission);
        EnrichStatus(classified, submission);

        AppendReviewFlags(submission);

        return submission;
    }

    private LineClassification[] ClassifyLines(string[] lines)
    {
        var result = new LineClassification[lines.Length];

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            var scu = ScuRegex.Match(line);
            if (scu.Success && IsLineMostlyNumber(line))
            {
                result[i] = new LineClassification(line, LineType.Scu, ScuValue: ParseScu(scu));
                continue;
            }

            var price = PriceRegex.Match(line);
            if (price.Success)
            {
                result[i] = new LineClassification(line, LineType.Price, PriceValue: ParsePrice(price));
                continue;
            }

            if (LooksLikeNameCandidate(line) && !IsUiLabel(line))
            {
                var singleMatch = _matcher.MatchCommodity(line, _commodityMinScore);

                CommodityMatch? concatMatch = null;
                string? concatRaw = null;
                int nextNameIdx = -1;
                for (var look = 1; look <= 4 && i + look < lines.Length; look++)
                {
                    var l = lines[i + look];
                    if (ScuRegex.IsMatch(l) || PriceRegex.IsMatch(l)) continue;
                    if (IsUiLabel(l)) continue;
                    if (!LooksLikeNameCandidate(l)) continue;
                    nextNameIdx = i + look;
                    break;
                }

                if (nextNameIdx > 0)
                {
                    var concat = $"{line} {lines[nextNameIdx]}";
                    if (!IsUiLabel(concat))
                    {
                        concatMatch = _matcher.MatchCommodity(concat, _commodityMinScore);
                        concatRaw = concat;
                    }
                }

                // Prefer concatenated match when the multi-line name beats the single-line one.
                if (concatMatch is not null
                    && (singleMatch is null
                        || concatMatch.Score >= singleMatch.Score + 5
                        || (singleMatch.Score < 95 && concatMatch.Score >= singleMatch.Score)))
                {
                    result[i] = new LineClassification(concatRaw!, LineType.Commodity, Commodity: concatMatch);
                    result[nextNameIdx] = new LineClassification("", LineType.Skip);
                    continue;
                }

                if (singleMatch is not null)
                {
                    result[i] = new LineClassification(line, LineType.Commodity, Commodity: singleMatch);
                    continue;
                }
            }

            result[i] ??= new LineClassification(line, LineType.Other);
        }

        return result;
    }

    private static void BuildPriceRows(LineClassification[] classified, ParsedSubmission submission)
    {
        ParsedPriceRow? current = null;

        foreach (var c in classified)
        {
            if (c is null) continue;

            switch (c.Type)
            {
                case LineType.Commodity when c.Commodity is not null:
                    if (current is not null)
                    {
                        submission.Prices.Add(current);
                    }
                    current = new ParsedPriceRow
                    {
                        IdCommodity = c.Commodity.Commodity.Id,
                        CommodityName = c.Commodity.Commodity.Name,
                        CommodityCode = c.Commodity.Commodity.Code,
                        CommodityMatchScore = c.Commodity.Score,
                        CommodityMatchedFromOcr = c.Commodity.FromOcr,
                    };
                    break;

                case LineType.Scu when current is not null && current.ScuBuy is null && c.ScuValue is not null:
                    current.ScuBuy = c.ScuValue;
                    current.RawScu = c.OriginalLine;
                    break;

                case LineType.Price when current is not null && current.PriceBuy is null && c.PriceValue is not null:
                    current.PriceBuy = c.PriceValue;
                    current.RawPrice = c.OriginalLine;
                    break;
            }
        }

        if (current is not null)
        {
            submission.Prices.Add(current);
        }
    }

    private static void DedupeRows(ParsedSubmission submission)
    {
        var grouped = submission.Prices
            .Where(r => r.IdCommodity is not null)
            .GroupBy(r => r.IdCommodity!.Value)
            .Select(g => g
                .OrderByDescending(r => Completeness(r))
                .ThenByDescending(r => r.CommodityMatchScore)
                .First())
            .ToList();

        submission.Prices = grouped;
    }

    private static int Completeness(ParsedPriceRow r)
        => (r.ScuBuy is not null ? 2 : 0) + (r.PriceBuy is not null ? 2 : 0);

    private static void EnrichStatus(LineClassification[] classified, ParsedSubmission submission)
    {
        if (submission.Prices.Count == 0) return;

        var statusByCommodityIdx = new Dictionary<int, (InventoryStatus Status, string RawLine)>();
        InventoryStatus? lastStatus = null;
        string? lastStatusLine = null;
        var rowIdx = -1;
        var seenCommodityIds = new HashSet<int>();

        foreach (var c in classified)
        {
            if (c is null) continue;

            if (c.Type == LineType.Commodity && c.Commodity is not null)
            {
                if (!seenCommodityIds.Add(c.Commodity.Commodity.Id)) continue;
                rowIdx++;
                if (lastStatus is not null && rowIdx < submission.Prices.Count)
                {
                    statusByCommodityIdx[rowIdx] = (lastStatus.Value, lastStatusLine ?? "");
                    lastStatus = null;
                    lastStatusLine = null;
                }
                continue;
            }

            foreach (var (pattern, status) in StatusPatterns)
            {
                if (pattern.IsMatch(c.OriginalLine))
                {
                    lastStatus = status;
                    lastStatusLine = c.OriginalLine;
                    break;
                }
            }

            if (lastStatus is not null && rowIdx >= 0 && rowIdx < submission.Prices.Count
                && submission.Prices[rowIdx].StatusBuy == InventoryStatus.Unknown)
            {
                submission.Prices[rowIdx].StatusBuy = lastStatus.Value;
                submission.Prices[rowIdx].RawStatus = lastStatusLine;
                lastStatus = null;
                lastStatusLine = null;
            }
        }

        foreach (var (idx, (status, raw)) in statusByCommodityIdx)
        {
            if (idx < submission.Prices.Count
                && submission.Prices[idx].StatusBuy == InventoryStatus.Unknown)
            {
                submission.Prices[idx].StatusBuy = status;
                submission.Prices[idx].RawStatus = raw;
            }
        }
    }

    private static TerminalTab DetectTab(string[] lines)
    {
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim().ToUpperInvariant();
            if (FuzzySharp.Fuzz.WeightedRatio(line, "LOCAL MARKET VALUE") >= 80)
            {
                if (i > 0)
                {
                    var prev = lines[i - 1].Trim().ToUpperInvariant();
                    if (FuzzySharp.Fuzz.WeightedRatio(prev, "BUY") >= 80) return TerminalTab.Buy;
                }
                return TerminalTab.Sell;
            }
            if (FuzzySharp.Fuzz.WeightedRatio(line, "BUY") >= 90 && line.Length <= 5)
            {
                return TerminalTab.Buy;
            }
        }
        return TerminalTab.Buy;
    }

    private static string? DetectContainerSizes(string[] lines)
    {
        HashSet<int>? bestSizes = null;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].ToUpperInvariant();
            var isCargoMarker = FuzzySharp.Fuzz.PartialRatio(line, "AVAILABLE CARGO SIZE") >= 75
                                 || FuzzySharp.Fuzz.PartialRatio(line, "CARGO SIZE") >= 90;
            if (!isCargoMarker) continue;

            for (var off = 0; off <= 3 && i + off < lines.Length; off++)
            {
                var candidateRaw = lines[i + off];
                var digits = new string(candidateRaw.Where(char.IsDigit).ToArray());
                if (digits.Length < 2) continue;

                var sizes = ExtractSizesGreedy(digits);
                if (sizes.Count >= 2 && (bestSizes is null || sizes.Count > bestSizes.Count))
                {
                    bestSizes = sizes;
                }
            }
        }

        if (bestSizes is null || bestSizes.Count == 0) return null;
        return string.Join(",", bestSizes.OrderBy(s => s));
    }

    private static HashSet<int> ExtractSizesGreedy(string digits)
    {
        var sizes = new HashSet<int>();
        var idx = 0;
        while (idx < digits.Length)
        {
            var matched = false;
            foreach (var v in KnownContainerSizes)
            {
                var s = v.ToString();
                if (idx + s.Length <= digits.Length && digits.Substring(idx, s.Length) == s)
                {
                    sizes.Add(v);
                    idx += s.Length;
                    matched = true;
                    break;
                }
            }
            if (!matched) idx++;
        }
        return sizes;
    }

    private static void AppendReviewFlags(ParsedSubmission submission)
    {
        if (submission.IdTerminal is null)
        {
            submission.NeedsReview.Add("terminal_not_detected");
        }
        else if (submission.TerminalMatchScore < 90)
        {
            submission.NeedsReview.Add($"terminal_match_low_confidence ({submission.TerminalMatchScore:F0})");
        }

        if (submission.Tab == TerminalTab.Unknown)
        {
            submission.NeedsReview.Add("tab_unknown");
        }
        else
        {
            submission.NeedsReview.Add($"tab_assumed_{submission.Tab.ToString().ToLowerInvariant()}");
        }

        for (var i = 0; i < submission.Prices.Count; i++)
        {
            var p = submission.Prices[i];
            if (p.CommodityMatchScore < 90)
            {
                submission.NeedsReview.Add($"row[{i}] {p.CommodityName} commodity_low_confidence ({p.CommodityMatchScore:F0})");
            }
            if (p.ScuBuy is null)
            {
                submission.NeedsReview.Add($"row[{i}] {p.CommodityName} scu_missing");
            }
            if (p.PriceBuy is null)
            {
                submission.NeedsReview.Add($"row[{i}] {p.CommodityName} price_missing");
            }
            if (p.StatusBuy == InventoryStatus.Unknown)
            {
                submission.NeedsReview.Add($"row[{i}] {p.CommodityName} status_unknown");
            }
        }
    }

    private static bool LooksLikeNameCandidate(string line)
    {
        if (line.Length < 3) return false;
        var letters = line.Count(char.IsLetter);
        if (letters < 3) return false;
        var upper = line.Count(c => char.IsUpper(c));
        return upper >= letters * 0.6;
    }

    private static bool IsUiLabel(string line)
    {
        var token = line.Trim().ToUpperInvariant();
        if (token.Length == 0) return true;

        foreach (var stop in UiStopWords)
        {
            var score = FuzzySharp.Fuzz.WeightedRatio(token, stop);
            if (score >= 80) return true;
        }
        return false;
    }

    private static bool IsLineMostlyNumber(string line)
    {
        if (line.Length == 0) return false;
        var digits = line.Count(char.IsDigit);
        return digits >= 1 && digits >= line.Length * 0.3;
    }

    private static int? ParseScu(Match match)
    {
        var raw = match.Groups["num"].Value;
        var clean = raw.Replace(",", "").Replace(".", "").Replace(" ", "");
        return int.TryParse(clean, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? v
            : null;
    }

    private static double? ParsePrice(Match match)
    {
        var raw = match.Groups["num"].Value;
        var unit = match.Groups["unit"].Value.ToUpperInvariant();

        double multiplier = unit switch
        {
            "K" => 1_000.0,
            "M" => 1_000_000.0,
            _ => 1.0,
        };

        string clean;
        if (multiplier > 1)
        {
            clean = raw.Replace(",", ".").Replace(" ", "");
            var dotIdx = clean.IndexOf('.');
            if (dotIdx > 0)
            {
                clean = clean[..(dotIdx + 1)] + clean[(dotIdx + 1)..].Replace(".", "");
            }
        }
        else
        {
            clean = raw.Replace(",", "").Replace(".", "").Replace(" ", "");
        }

        return double.TryParse(clean, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? v * multiplier
            : null;
    }

    private sealed record LineClassification(
        string OriginalLine,
        LineType Type,
        CommodityMatch? Commodity = null,
        TerminalMatch? Terminal = null,
        int? ScuValue = null,
        double? PriceValue = null);

    private enum LineType
    {
        Other,
        Skip,
        Commodity,
        Scu,
        Price,
    }
}
