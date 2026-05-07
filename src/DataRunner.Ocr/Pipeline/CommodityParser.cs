using System.Globalization;
using System.Text;
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

    /// <summary>
    /// Recognises an isolated "0 SCU"-shaped token where the leading 0 was
    /// either kept as a digit OR misread as letter 'O' (very common — SC
    /// renders the zero in a small font that PaddleOCR cannot disambiguate
    /// from O on dark backgrounds). Accepts all four spellings:
    /// <c>0 SCU</c>, <c>O SCU</c>, <c>0SCU</c>, <c>OSCU</c>, plus the
    /// S↔5 OCR confusion (<c>05CU</c>, <c>O5CU</c>).
    /// The standard <see cref="ScuRegex"/> requires a leading digit so it
    /// silently drops the 'O'-prefixed variants and the row ends up with
    /// no SCU value, which then prevents InferOutOfStockState from setting
    /// SCU=0 unless the status detector also fired.
    /// </summary>
    private static readonly Regex IsolatedZeroScuRegex = new(
        @"^[O0o]\s*[5Ss][Cc][UuOo0]\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Quick reject filter for the OUT-OF-STOCK fuzzy fallback: the line
    /// must contain a "STOCK"-like token (with the K↔H OCR substitution
    /// tolerated). Without this guard the fuzzy ratio check would be run
    /// against every status line on the panel and could false-positive on
    /// random commodity names sharing a few letters with "OUT OF STOCK".
    /// </summary>
    private static readonly Regex StockishRegex = new(
        @"ST[O0]C[KH]?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

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

    // Helper: tolerated character classes for OCR-typical confusions inside
    // the word "INVENTORY". I↔1, E↔3, O↔0 are the most common substitutions.
    private const string InvWord = @"[I1]NV[E3]NT[O0]RY";
    // Separator between the modifier word ("MAX") and "INVENTORY". OCR often
    // injects a stray period or comma here, eg. "MAX. INVENTORY" or
    // "MAX,INVENTORY". We also allow zero whitespace ("MAXINVENTORY"), which
    // happens when PaddleOCR fuses two adjacent text regions on the same Y.
    private const string Sep = @"[.,;:\s]*";

    /// <summary>
    /// Strict regex patterns for inventory status detection. We tried fuzzy
    /// PartialRatio matching against canonical phrases but it cross-matched
    /// "MAX INVENTORY" with "MEDIUM INVENTORY" because they share the long
    /// "INVENTORY" suffix (~80% substring overlap). Strict regex with
    /// explicit word boundaries is the right primitive here; we sprinkle in
    /// well-known OCR substitutions to absorb typical recognition noise:
    ///   - [MN] for the leading letter of MAX/MEDIUM (M↔N is a frequent
    ///     PaddleOCR confusion when an icon abuts the text on the left,
    ///     observed on the "bag-icon" rows in SC's Daekens Research Outpost).
    ///   - [.,;:\s]* between the modifier and INVENTORY (catches "MAX.
    ///     INVENTORY", "MAXINVENTORY", "MAX,INVENTORY").
    ///   - 1↔I, 3↔E, 0↔O inside INVENTORY itself.
    /// When even this fails the status stays Unknown and the user fixes it
    /// via the orange-tinted cell in the editor.
    /// </summary>
    private static readonly (Regex Pattern, InventoryStatus Status)[] StatusPatterns =
    {
        (new Regex($@"\b(?:MAXIMUM|[MN]AX){Sep}{InvWord}\b|\bFULL\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), InventoryStatus.Maximum),
        (new Regex($@"\bVERY{Sep}HIGH{Sep}{InvWord}\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), InventoryStatus.VeryHigh),
        (new Regex($@"\bHIGH{Sep}{InvWord}\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), InventoryStatus.High),
        (new Regex($@"\b(?:[MN]EDIUM|[MN]ED){Sep}{InvWord}\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), InventoryStatus.Medium),
        (new Regex($@"\bVERY{Sep}LOW{Sep}{InvWord}\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), InventoryStatus.VeryLow),
        (new Regex($@"\bLOW{Sep}{InvWord}\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), InventoryStatus.Low),
        (new Regex(@"\bOUT\s*OF\s*ST[O0]CK\b|\bEMPTY\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), InventoryStatus.OutOfStock),
    };

    // SC always renders container sizes in ASCENDING order in the cargo bar:
    //   "1 2 4 8 16 24 32"
    // We rely on this to disambiguate digit runs like "124816" which a naive
    // greedy "longest first" approach would parse as {1, 24, 8, 16}.
    // See ExtractSizesGreedy for details.
    private static readonly int[] KnownContainerSizesAscending = { 1, 2, 4, 8, 16, 24, 32 };

    private readonly FuzzyMatcher _matcher;
    private readonly int _commodityMinScore;

    // Threshold tuned together with FuzzyMatcher.ScoreCommodityCandidate length
    // penalty. With the penalty in place, false positives like "STINS" -> "TIN"
    // are filtered out cleanly (54%), so we can accept legitimate near-matches
    // like "STINS" -> "STIMS" (~80%) which used to be silently dropped at the
    // old 85% threshold. The UI then flags 75-99% as "Warning" or "Error" so
    // the user explicitly validates the choice before submission.
    public CommodityParser(FuzzyMatcher matcher, int commodityMinScore = 75)
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
        InferOutOfStockState(submission);

        AppendReviewFlags(submission);

        return submission;
    }

    private LineClassification[] ClassifyLines(string[] lines)
    {
        var result = new LineClassification[lines.Length];

        for (var i = 0; i < lines.Length; i++)
        {
            var rawLine = lines[i];

            // Normalize ambiguous OCR'd characters in numeric-looking tokens ONLY:
            // "B0OO" -> "8000", "30OO" -> "3000", "lI20" -> "1120", etc.
            // Pure-letter tokens (commodity names like "BORON") are intentionally
            // left untouched, see NormalizeNumericTokens for the heuristic.
            var line = NormalizeNumericTokens(rawLine);

            var scu = ScuRegex.Match(line);
            if (scu.Success && IsLineMostlyNumber(line))
            {
                result[i] = new LineClassification(line, LineType.Scu, ScuValue: ParseScu(scu));
                continue;
            }

            // Out-of-stock items render their quantity as a tiny "0 SCU"
            // that PaddleOCR collapses into a single 4-character token —
            // typically "OSCU" (zero misread as letter O) or "0SCU"
            // (digit retained but space dropped). IsLineMostlyNumber
            // would reject such short, mostly-letter tokens, so we
            // catch them with an explicit pattern before falling
            // through to the name-candidate branch.
            if (IsolatedZeroScuRegex.IsMatch(line.Trim()))
            {
                result[i] = new LineClassification(line, LineType.Scu, ScuValue: 0);
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
        // True after we cross a "--- ROW i ---" band marker but before the next
        // commodity is encountered. While locked, status patterns may still be
        // detected (so they can fall through to a future commodity in this band)
        // but they are NEVER assigned back to the previous band's commodity.
        // This stops section headers like "OUT OF STOCK" at the bottom of the
        // panel from poisoning the last real commodity row when its in-band
        // status was missed by the OCR.
        var assignmentLocked = false;

        foreach (var c in classified)
        {
            if (c is null) continue;

            // Band barrier emitted by PaddleOcrPipeline when row segmentation is
            // active. Reset every status carry so nothing leaks between bands.
            if (c.OriginalLine.StartsWith("--- ROW ", StringComparison.Ordinal))
            {
                lastStatus = null;
                lastStatusLine = null;
                assignmentLocked = true;
                continue;
            }

            if (c.Type == LineType.Commodity && c.Commodity is not null)
            {
                if (!seenCommodityIds.Add(c.Commodity.Commodity.Id)) continue;
                rowIdx++;
                // We have a commodity in the current band; future status patterns
                // can now be assigned to it.
                assignmentLocked = false;
                if (lastStatus is not null && rowIdx < submission.Prices.Count)
                {
                    statusByCommodityIdx[rowIdx] = (lastStatus.Value, lastStatusLine ?? "");
                    lastStatus = null;
                    lastStatusLine = null;
                }
                continue;
            }

            var matchedByRegex = false;
            foreach (var (pattern, status) in StatusPatterns)
            {
                if (pattern.IsMatch(c.OriginalLine))
                {
                    lastStatus = status;
                    lastStatusLine = c.OriginalLine;
                    matchedByRegex = true;
                    break;
                }
            }

            // OUT-OF-STOCK fuzzy fallback. Real captures of out-of-stock
            // rows produce variants the strict regex cannot handle —
            // "OUT OF STOCH" (K→H), "QUT DE STOCK" (O→Q + OF→DE),
            // "QUTAOF STOCK" (extra glyph), etc. — observed on
            // People's Service Station Theta + several Pyro stations.
            // Fuzzy matching is safe here because "OUT OF STOCK" is the
            // ONLY status whose canonical phrase doesn't share a long
            // suffix with another status (vs. the INVENTORY family
            // which all share "INVENTORY" and would cross-match).
            if (!matchedByRegex && LooksLikeOutOfStockFuzzy(c.OriginalLine))
            {
                lastStatus = InventoryStatus.OutOfStock;
                lastStatusLine = c.OriginalLine;
                matchedByRegex = true;
            }

            // INVENTORY-FAMILY fuzzy fallback. Pyro screens render status
            // labels in TitleCase ("Medium Inventory" instead of Stanton's
            // "MEDIUM INVENTORY") and the lower-case rendering combined
            // with the red panel background degrades OCR enough that we
            // see substitutions the strict regex cannot absorb — eg. the
            // I↔S misread on the lower-case "i" of Medium → "Medsum
            // Inventory" observed on Pyro Endgame on 2026-05-07. Rather
            // than chasing every per-letter confusion in the regex
            // alternations (which risks cross-matching MAX↔MEDIUM
            // through their shared INVENTORY suffix), we extract the
            // MODIFIER prefix on its own and fuzzy-match THAT short
            // word against the canonical set. The INVENTORY suffix
            // detection itself is still tolerant of OCR substitutions
            // via TryExtractInventoryModifier's regex.
            if (!matchedByRegex && TryFuzzyMatchInventoryStatus(c.OriginalLine) is { } fuzzyStatus)
            {
                lastStatus = fuzzyStatus;
                lastStatusLine = c.OriginalLine;
            }

            if (!assignmentLocked
                && lastStatus is not null && rowIdx >= 0 && rowIdx < submission.Prices.Count
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

    /// <summary>
    /// Container sizes are a TERMINAL-LEVEL field in the UEX API, but each SC
    /// commodity row only renders the subset of sizes appropriate for that
    /// commodity's quantity. We therefore scan EVERY "AVAILABLE CARGO SIZE"
    /// line in the OCR output and return the UNION of all detected sizes.
    ///
    /// Why union (and not "row with the most sizes"): OCR sometimes loses the
    /// dimmer / smaller / off-canvas sizes on a given row (eg. the "24 32" at
    /// the right edge for a high-quantity commodity). Taking the union recovers
    /// values that were correctly read on a different row of the same terminal.
    /// </summary>
    private static string? DetectContainerSizes(string[] lines)
    {
        var unionSizes = new HashSet<int>();

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
                if (sizes.Count >= 2)
                {
                    unionSizes.UnionWith(sizes);
                }
            }
        }

        if (unionSizes.Count == 0) return null;
        return string.Join(",", unionSizes.OrderBy(s => s));
    }

    /// <summary>
    /// Parses a continuous digit run produced by joining the cargo-size pills
    /// from a SC commodity row into a set of recognized container sizes.
    /// Walks the known sizes in ASCENDING order, consuming the prefix of the
    /// remaining digits each time a size matches. This disambiguates cases
    /// where the previous "longest-first" greedy logic would mis-merge digits.
    ///
    /// Examples:
    ///   "124816"     -> {1, 2, 4, 8, 16}        (no false 24 introduced)
    ///   "1248162432" -> {1, 2, 4, 8, 16, 24, 32}
    ///   "48162432"   -> {4, 8, 16, 24, 32}      (small sizes dimmed/missed by OCR
    ///                                            still produce a valid subset)
    /// </summary>
    private static HashSet<int> ExtractSizesGreedy(string digits)
    {
        var sizes = new HashSet<int>();
        var idx = 0;
        foreach (var size in KnownContainerSizesAscending)
        {
            var s = size.ToString();
            if (idx + s.Length > digits.Length) break;
            if (digits.AsSpan(idx, s.Length).SequenceEqual(s))
            {
                sizes.Add(size);
                idx += s.Length;
            }
        }
        return sizes;
    }

    /// <summary>
    /// Reconciles SCU and inventory-status fields on Out-of-Stock rows.
    /// "Out of Stock" is by definition 0 SCU, so both fields imply each
    /// other and the inference goes both ways:
    ///
    ///   FORWARD  : <c>StatusBuy == OutOfStock &amp;&amp; ScuBuy is null</c>
    ///              → <c>ScuBuy = 0</c>.
    ///              SC renders OOS rows with a tiny "0" PaddleOCR often
    ///              fails to read, so the field is left null even though
    ///              the status was clearly detected.
    ///
    ///   REVERSE  : <c>ScuBuy == 0 &amp;&amp; StatusBuy == Unknown</c>
    ///              → <c>StatusBuy = OutOfStock</c>.
    ///              The opposite OCR failure: the "0 SCU" was read but
    ///              the "OUT OF STOCK" header was either off-screen
    ///              (Hydrogen at the bottom of the panel in
    ///              terminal_screenshot-5.jpg) or so corrupted that
    ///              even the fuzzy fallback in EnrichStatus did not
    ///              fire. SCU=0 with no status only ever means OOS in
    ///              the SC commodity terminal.
    ///
    /// Only acts when the destination field is empty/Unknown — never
    /// overwrites a value the upstream stages already determined. Other
    /// statuses (Low, Medium, High, Maximum) can legitimately carry ANY
    /// positive SCU value so they never trigger either direction.
    /// </summary>
    private static void InferOutOfStockState(ParsedSubmission submission)
    {
        foreach (var row in submission.Prices)
        {
            if (row.StatusBuy == InventoryStatus.OutOfStock && row.ScuBuy is null)
            {
                row.ScuBuy = 0;
                row.RawScu ??= "(inferred: OutOfStock → 0 SCU)";
            }
            else if (row.ScuBuy == 0 && row.StatusBuy == InventoryStatus.Unknown)
            {
                row.StatusBuy = InventoryStatus.OutOfStock;
                row.RawStatus ??= "(inferred: 0 SCU → OutOfStock)";
            }
        }
    }

    /// <summary>
    /// Tolerant fuzzy match for the "OUT OF STOCK" status header.
    /// Returns true when <paramref name="line"/> is plausibly that
    /// header even after typical OCR corruption.
    ///
    /// Three guards against false positives:
    ///   1. Length window 10..20 — rules out the unrelated "IN STOCK"
    ///      section header (8 chars) which would otherwise score high
    ///      via PartialRatio on the shared "STOCK" substring.
    ///   2. Must contain a "STOCK"-like token (with K↔H tolerance) —
    ///      ensures the line is actually about stock state and not
    ///      another short phrase that happens to fuzzy-score highly.
    ///   3. WeightedRatio ≥ 70 against the canonical phrase — covers
    ///      OCR errors of up to ~3 characters ("QUT DE STOCK" sits at
    ///      this threshold; everything closer scores higher).
    /// </summary>
    private static bool LooksLikeOutOfStockFuzzy(string line)
    {
        var trimmed = line.Trim().ToUpperInvariant();
        if (trimmed.Length is < 10 or > 20) return false;
        if (!StockishRegex.IsMatch(trimmed)) return false;
        return FuzzySharp.Fuzz.WeightedRatio(trimmed, "OUT OF STOCK") >= 70;
    }

    /// <summary>
    /// Tolerant suffix detector for the word "INVENTORY" with the
    /// frequent OCR substitutions baked in (I↔1, E↔3, O↔0, R sometimes
    /// dropped on tight letter spacing). Used as the gate before we
    /// fuzzy-match the modifier prefix against the canonical status
    /// list, so the fuzzy fallback only fires on lines that are
    /// actually inventory labels.
    /// </summary>
    private static readonly Regex InventorySuffixRegex = new(
        @"\b[I1][N][V][E3][N][T][O0][R]?[Y]\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Canonical modifier strings ordered MOST-SPECIFIC first so longer
    /// phrases ("VERY HIGH") always win over their substrings ("HIGH").
    /// FUZZY threshold is calibrated per modifier in
    /// <see cref="TryFuzzyMatchInventoryStatus"/>.
    /// </summary>
    private static readonly (string Canonical, InventoryStatus Status)[] InventoryModifiers =
    {
        ("VERY HIGH", InventoryStatus.VeryHigh),
        ("VERY LOW", InventoryStatus.VeryLow),
        ("MAXIMUM", InventoryStatus.Maximum),
        ("MEDIUM", InventoryStatus.Medium),
        ("HIGH", InventoryStatus.High),
        ("LOW", InventoryStatus.Low),
        ("MAX", InventoryStatus.Maximum),
        ("MED", InventoryStatus.Medium),
        ("FULL", InventoryStatus.Maximum),
    };

    /// <summary>
    /// Fuzzy fallback for inventory-status lines the strict
    /// <see cref="StatusPatterns"/> regex couldn't match. The pipeline
    /// only invokes this AFTER the regex has failed, so accuracy here
    /// is a strict win over leaving the row at <c>Unknown</c>.
    ///
    /// Strategy:
    ///   1. Confirm the line is genuinely an inventory label by
    ///      checking for an "INVENTORY"-like suffix
    ///      (<see cref="InventorySuffixRegex"/>). Without this guard
    ///      we would fuzzy-match short prefixes against random
    ///      commodity names that happen to share a few letters
    ///      with a modifier.
    ///   2. Slice off everything from the INVENTORY token onward and
    ///      take what's LEFT as the modifier candidate. SC always
    ///      renders the pattern "<MODIFIER> INVENTORY", never the
    ///      reverse.
    ///   3. Fuzzy-score the modifier candidate against each canonical
    ///      modifier; accept if the best score ≥ 75 (calibrated from
    ///      the 2026-05-07 Pyro logs where "Medsum" → "MEDIUM" sits
    ///      at ~83% WeightedRatio).
    ///
    /// Returns null if no canonical modifier scores high enough — the
    /// caller leaves the row at <c>Unknown</c> for manual fixup.
    /// </summary>
    private static InventoryStatus? TryFuzzyMatchInventoryStatus(string line)
    {
        var inventoryMatch = InventorySuffixRegex.Match(line);
        if (!inventoryMatch.Success) return null;

        var prefix = line[..inventoryMatch.Index].Trim().ToUpperInvariant();
        if (prefix.Length is < 2 or > 24) return null;

        InventoryStatus? best = null;
        var bestScore = 0;

        foreach (var (canonical, status) in InventoryModifiers)
        {
            // PartialRatio absorbs leading icon glyphs / stray digits
            // PaddleOCR sometimes prepends to the modifier on tight
            // panel layouts. WeightedRatio rejects them too aggressively
            // when prefix is short (≤ 6 chars) — partial is the right
            // primitive when modifier is a sub-token of a larger
            // OCR-merged region.
            var score = FuzzySharp.Fuzz.PartialRatio(prefix, canonical);
            if (score > bestScore)
            {
                bestScore = score;
                best = status;
            }
        }

        // 75 is the empirical floor: "MEDSUM" vs "MEDIUM" partial-scores
        // at 83 (5/6 chars match), "MAXE" vs "MAX" at 100, and pure
        // noise like commodity name fragments stay below 65. The
        // canonical-list ordering above guarantees longer phrases
        // ("VERY HIGH") match before their substrings ("HIGH") when
        // both score equally.
        return bestScore >= 75 ? best : null;
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

    /// <summary>
    /// Heuristic gate that decides whether a raw OCR line is a plausible
    /// commodity-name candidate before we hand it to the fuzzy matcher.
    /// We accept three rendering styles seen across SC themes:
    ///  • <b>UPPERCASE</b> — Stanton stations use this exclusively
    ///    (<c>"LARANITE"</c>, <c>"MEDICAL SUPPLIES"</c>).
    ///  • <b>Title Case</b> — Pyro stations render names with only the
    ///    first letter of each word capitalised (<c>"Agricium"</c>,
    ///    <c>"Medical Supplies"</c>, <c>"Altruciatoxin"</c>). The previous
    ///    "≥60% uppercase" rule rejected these outright, which is why
    ///    Pyro screenshots produced rows=0 even though the OCR text was
    ///    perfectly readable.
    ///  • <b>Mixed-case fragments from OCR noise</b> — only when the FIRST
    ///    letter of every word is uppercase. Anything looking like a
    ///    sentence/log line (eg. <c>"Welcome to the trading post"</c>)
    ///    fails the per-word capitalisation test.
    /// </summary>
    private static bool LooksLikeNameCandidate(string line)
    {
        if (line.Length < 3) return false;
        var letters = line.Count(char.IsLetter);
        if (letters < 3) return false;

        // STYLE 1: mostly UPPERCASE (Stanton). Same threshold as before
        // so existing screenshots keep matching identically.
        var upper = line.Count(c => char.IsUpper(c));
        if (upper >= letters * 0.6) return true;

        // STYLE 2: per-word Title Case (Pyro). Every word that contains
        // letters must start with an uppercase letter. We tolerate digits
        // and a small number of stray-leading-symbol tokens (icon glyphs
        // PaddleOCR sometimes prepends to a name) by skipping any word
        // whose first letter isn't… a letter at all.
        var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return false;
        var titleWords = 0;
        var lettered = 0;
        foreach (var w in words)
        {
            var firstLetter = w.FirstOrDefault(char.IsLetter);
            if (firstLetter == default) continue;
            lettered++;
            if (char.IsUpper(firstLetter)) titleWords++;
        }
        // At least one lettered word AND every lettered word starts uppercase.
        return lettered > 0 && titleWords == lettered;
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

    /// <summary>
    /// Normalize OCR-ambiguous characters (O->0, l/I/|->1, S->5, B->8, Z->2)
    /// inside tokens that already look numeric, leaving alphabetic tokens alone.
    ///
    /// Heuristic per token: substitute only if the token has at least one digit
    /// AND (digits + ambiguous chars) cover &gt;= 70% of its length AND there is
    /// no more than one "real" letter. This protects commodity names like BORON,
    /// ZEROES, STIMS from being corrupted into 80R0N / 2ER0E5 / 5T1M5.
    ///
    /// Examples:
    ///   "B0OO"      -> "8000"    (1 digit + 3 ambig, 0 letters)
    ///   "30OO"      -> "3000"    (2 digits + 2 ambig, 0 letters)
    ///   "lI20"      -> "1120"    (2 digits + 2 ambig, 0 letters)
    ///   "5S00"      -> "5500"    (3 digits + 1 ambig, 0 letters)
    ///   "120/SCU"   -> unchanged (3 digits + 1 ambig + 2 letters + 1 slash)
    ///   "BORON"     -> unchanged (no digits at all)
    /// </summary>
    private static string NormalizeNumericTokens(string line)
    {
        if (string.IsNullOrEmpty(line)) return line;

        var tokens = line.Split(' ');
        var changed = false;

        for (var i = 0; i < tokens.Length; i++)
        {
            var t = tokens[i];
            if (t.Length < 2) continue;

            var digits = 0;
            var ambig = 0;
            var realLetters = 0;
            foreach (var ch in t)
            {
                if (char.IsDigit(ch)) digits++;
                else if (IsAmbiguousChar(ch)) ambig++;
                else if (char.IsLetter(ch)) realLetters++;
            }

            if (digits == 0) continue;
            if (realLetters > 1) continue;
            if ((double)(digits + ambig) / t.Length < 0.7) continue;

            var sb = new StringBuilder(t.Length);
            foreach (var ch in t)
            {
                sb.Append(ch switch
                {
                    'O' or 'o' => '0',
                    'l' or 'I' or '|' => '1',
                    'S' => '5',
                    'B' => '8',
                    'Z' => '2',
                    _ => ch,
                });
            }
            tokens[i] = sb.ToString();
            changed = true;
        }

        return changed ? string.Join(' ', tokens) : line;
    }

    private static bool IsAmbiguousChar(char ch) => ch switch
    {
        'O' or 'o' or 'l' or 'I' or '|' or 'S' or 'B' or 'Z' => true,
        _ => false,
    };

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
