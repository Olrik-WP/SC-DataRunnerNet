using System.Diagnostics;
using System.Text;
using DataRunner.Core.Abstractions;
using DataRunner.Core.Models;
using DataRunner.Ocr.Matching;
using DataRunner.Ocr.Pipeline;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using Sdcb.PaddleOCR;
using Sdcb.PaddleOCR.Models;
using Sdcb.PaddleOCR.Models.Online;

namespace DataRunner.Ocr;

/// <summary>
/// Composite pipeline engine for SC commodity terminals.
/// Two-pass OCR strategy:
///   1) TOP banner pass    -> terminal name (e.g. "EVERUS HARBOR")
///   2) LEFT header pass   -> terminal name fallback (e.g. "HICKES RESEARCH OUTPOST")
///   3) RIGHT panel pass   -> commodities + prices + status + container sizes
/// Results are merged into a ParsedSubmission and a UEX-shaped UexDataSubmitPayload.
///
/// Heavy native resources (PaddleOCR, OpenCV) live for the whole app lifetime via
/// <see cref="PaddleOcrPipelineFactory"/> (singleton). Run() is thread-safe behind a lock
/// because PaddleOcrAll is NOT re-entrant.
/// </summary>
public sealed class PaddleOcrPipeline : IOcrPipeline, IDisposable
{
    public string Name => "PaddleOCR-Pipeline";

    private readonly PaddleOcrAll _ocr;
    private readonly CommodityParser _parser;
    private readonly FuzzyMatcher _matcher;
    private readonly ILogger<PaddleOcrPipeline> _logger;
    private readonly object _runLock = new();

    internal PaddleOcrPipeline(
        PaddleOcrAll ocr,
        CommodityParser parser,
        FuzzyMatcher matcher,
        ILogger<PaddleOcrPipeline> logger)
    {
        _ocr = ocr;
        _parser = parser;
        _matcher = matcher;
        _logger = logger;
    }

    public Task<OcrPipelineResult> RunAsync(string imagePath, CancellationToken ct = default)
        => Task.Run(() => RunCore(imagePath, ct), ct);

    private OcrPipelineResult RunCore(string imagePath, CancellationToken ct)
    {
        if (!File.Exists(imagePath))
            throw new FileNotFoundException("Source screenshot not found.", imagePath);

        ct.ThrowIfCancellationRequested();
        var sw = Stopwatch.StartNew();

        using var src = Cv2.ImRead(imagePath, ImreadModes.Color);
        if (src.Empty())
            throw new InvalidOperationException($"Cannot read image: {imagePath}");

        var combinedText = new StringBuilder();
        var confidences = new List<double>();

        TerminalMatch? bannerMatch;
        TerminalMatch? leftHeaderMatch = null;
        string rightRaw;
        TerminalTab detectedActiveTab = TerminalTab.Unknown;

        // PaddleOcrAll.Run is NOT re-entrant; serialise per-instance.
        lock (_runLock)
        {
            using (var topBand = ImagePreprocessor.ExtractTerminalNameBand(src))
            {
                ct.ThrowIfCancellationRequested();
                var topResult = _ocr.Run(topBand);
                var topRaw = RegionLayout.JoinByRows(topResult.Regions);
                if (topResult.Regions.Length > 0)
                    confidences.AddRange(topResult.Regions.Select(r => (double)r.Score));

                var topLines = topRaw.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
                bannerMatch = _matcher.MatchTerminalAcrossLines(topLines, minScore: 65);
                combinedText.AppendLine("--- TOP BANNER PASS ---").AppendLine(topRaw);
            }

            if (bannerMatch is null || bannerMatch.Score < 90)
            {
                using var leftHeader = ImagePreprocessor.ExtractLeftPanelHeader(src);
                ct.ThrowIfCancellationRequested();
                var leftResult = _ocr.Run(leftHeader);
                var leftRaw = RegionLayout.JoinByRows(leftResult.Regions);
                if (leftResult.Regions.Length > 0)
                    confidences.AddRange(leftResult.Regions.Select(r => (double)r.Score));

                var leftLines = leftRaw.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
                leftHeaderMatch = _matcher.MatchTerminalAcrossLines(leftLines, minScore: 65);
                combinedText.AppendLine("--- LEFT HEADER PASS (terminal name only) ---").AppendLine(leftRaw);
            }

            using (var preprocessed = ImagePreprocessor.Enhance(src, upscale: true, cropRightPanel: true))
            {
                ct.ThrowIfCancellationRequested();

                // ROW SEGMENTATION DISABLED.
                //
                // We tried two approaches to split the right panel into one Mat
                // per commodity card before OCR:
                //   1. Density-based gap detection (Otsu binarize + count bright
                //      pixels per row + threshold).
                //   2. Luminance-based gap detection (per-row grayscale mean +
                //      adaptive threshold on dynamic range).
                //
                // Both proved fragile on real SC screenshots: the panel has a
                // gradient background, varying icon brightness per row, and
                // intra-card sub-rows that look like inter-card gaps to either
                // signal. We end up either with 0 bands (silent fallback) or —
                // worse — a small number of HUGE bands that cover the wrong
                // pixels, which makes Paddle's detector miss most of the text.
                //
                // Single-pass OCR on the full preprocessed right panel returns
                // the right text consistently (the user's three reference
                // screenshots all extract their 3-4 commodities cleanly). The
                // remaining status-detection issues we observed (eg. "MAX. " /
                // "NAX " misreads on Daekens) were genuinely OCR errors and
                // are now absorbed by the tolerant status regex in
                // CommodityParser.StatusPatterns.
                //
                // Keeping the band-barrier handling in CommodityParser as a
                // no-op for the single-pass case (no "--- ROW i ---" markers
                // are emitted), so we can re-enable per-band OCR later (eg.
                // after fine-tuning the recognizer) by re-introducing the
                // band-loop here, with no changes to the parser.
                var ocrResult = _ocr.Run(preprocessed);
                if (ocrResult.Regions.Length > 0)
                {
                    confidences.AddRange(ocrResult.Regions.Select(r => (double)r.Score));
                }
                rightRaw = RegionLayout.JoinByRows(ocrResult.Regions);
                _logger.LogInformation(
                    "Right-panel OCR ({W}x{H}, {Regions} regions): {Text}",
                    preprocessed.Width, preprocessed.Height,
                    ocrResult.Regions.Length,
                    rightRaw.Replace("\n", " | "));

                combinedText.AppendLine("--- RIGHT PANEL PASS ---").AppendLine(rightRaw);

                // Saturation-based active-tab detection. The OCR text
                // alone cannot tell BUY from LOCAL MARKET VALUE because
                // both labels are always rendered on the tab bar; we
                // mirror the visual signal the user sees in-game
                // (saturated theme colour on the active tab — teal on
                // most stations, amber/orange on a handful of faction
                // terminals — vs. grey/white on the inactive one). The
                // discrimination is hue-agnostic on purpose. Runs against
                // the ORIGINAL src image — not the CLAHE preprocessed
                // Mat — to preserve true colour saturation.
                var rightPanelStartX = ImagePreprocessor.GetRightPanelStartX(src);
                var rightPanelWidth = src.Width - rightPanelStartX;
                var scaleFactor = rightPanelWidth > 0
                    ? preprocessed.Width / (double)rightPanelWidth
                    : 1.0;
                detectedActiveTab = TabDetector.DetectActiveTab(
                    src, ocrResult.Regions, rightPanelStartX, scaleFactor);
                _logger.LogInformation(
                    "Active-tab detection (color-based): {Tab} (scale={Scale:F2})",
                    detectedActiveTab, scaleFactor);
            }
        }

        var preferredTerminalMatch = ChooseBetter(bannerMatch, leftHeaderMatch);

        var submission = _parser.Parse(rightRaw, Path.GetFileName(imagePath));
        submission.SourceImageWidth = src.Width;
        submission.SourceImageHeight = src.Height;

        // Override the parser's text-only tab guess (which, by design, can
        // never reliably distinguish active from inactive labels) with the
        // colour-based decision when we have one. Only trust a confident
        // result; otherwise leave the parser's fallback in place.
        if (detectedActiveTab is TerminalTab.Buy or TerminalTab.Sell)
        {
            submission.Tab = detectedActiveTab;
            submission.NeedsReview.RemoveAll(f =>
                f.StartsWith("tab_assumed_", StringComparison.Ordinal)
                || f == "tab_unknown");
            submission.NeedsReview.Add(
                $"tab_detected_{detectedActiveTab.ToString().ToLowerInvariant()}");
        }

        if (preferredTerminalMatch is not null
            && (submission.IdTerminal is null
                || preferredTerminalMatch.Score > submission.TerminalMatchScore))
        {
            var sourceLabel = ReferenceEquals(preferredTerminalMatch, bannerMatch) ? "banner" : "left_header";
            submission.IdTerminal = preferredTerminalMatch.Terminal.Id;
            submission.TerminalDisplayName = preferredTerminalMatch.Terminal.DisplayName;
            submission.TerminalMatchScore = preferredTerminalMatch.Score;
            submission.TerminalMatchedFromOcr = preferredTerminalMatch.FromOcr;
            submission.TerminalMatchedField = $"{sourceLabel}:{preferredTerminalMatch.MatchedField}";
            CleanReviewFlagsForTerminal(submission);
        }

        // SECOND-CHANCE OCR PASS — fires only when the default pass left
        // gaps that we have a realistic shot at recovering. Two triggers:
        //   1. Terminal still unidentified (NULL after both banner + left header).
        //   2. At least one commodity row has Status == Unknown (UEX rejects
        //      submissions with `missing_inventory_status` so we MUST try harder
        //      before giving up and asking the user to fix it manually).
        // The retry uses ImagePreprocessor.EnhanceAggressive — same OCR engine,
        // tighter CLAHE + unsharp mask + ×3 upscale instead of ×2. Recovered
        // values are MERGED into the existing submission (we never overwrite a
        // good first-pass result with a worse retry result).
        var retryReasons = DescribeRetryReasons(submission);
        if (retryReasons.Length > 0)
        {
            _logger.LogInformation(
                "OCR retry triggered for {Img}: {Reasons}",
                Path.GetFileName(imagePath), string.Join(", ", retryReasons));
            RunRetryPasses(src, submission, ct, confidences, combinedText);
        }

        var payload = UexPayloadBuilder.Build(submission);

        sw.Stop();

        var meanConf = confidences.Count > 0 ? confidences.Average() : 0.0;
        var ocrEnvelope = new OcrResult(
            EngineName: Name,
            Text: combinedText.ToString(),
            MeanConfidence: meanConf,
            ElapsedMs: sw.ElapsedMilliseconds);

        _logger.LogInformation(
            "OCR pipeline {Img} -> terminal={Terminal} score={Score} rows={Rows} elapsed={Ms}ms",
            Path.GetFileName(imagePath),
            submission.TerminalDisplayName ?? "(unknown)",
            submission.TerminalMatchScore,
            submission.Prices.Count,
            sw.ElapsedMilliseconds);

        return new OcrPipelineResult(ocrEnvelope, submission, payload);
    }

    private static TerminalMatch? ChooseBetter(TerminalMatch? a, TerminalMatch? b)
    {
        if (a is null) return b;
        if (b is null) return a;
        return b.Score > a.Score ? b : a;
    }

    private static string[] DescribeRetryReasons(ParsedSubmission submission)
    {
        var reasons = new List<string>();
        if (submission.IdTerminal is null)
        {
            reasons.Add("terminal_not_detected");
        }
        var unknownStatusCount = submission.Prices.Count(p => p.StatusBuy == InventoryStatus.Unknown);
        if (unknownStatusCount > 0)
        {
            reasons.Add($"status_unknown_x{unknownStatusCount}");
        }
        return reasons.ToArray();
    }

    /// <summary>
    /// Runs the aggressive-preprocessing variants on the same source image and
    /// merges any recovered values into <paramref name="submission"/>. We only
    /// FILL holes — we never overwrite a value the first pass already set,
    /// because the aggressive preprocessing trades accuracy for recall and a
    /// good first-pass match should always win.
    /// </summary>
    private void RunRetryPasses(
        Mat src,
        ParsedSubmission submission,
        CancellationToken ct,
        List<double> confidences,
        StringBuilder combinedText)
    {
        var retrySw = Stopwatch.StartNew();

        // === RETRY 1: terminal name (banner + left header) ===
        if (submission.IdTerminal is null)
        {
            TerminalMatch? recoveredBanner = null;
            TerminalMatch? recoveredLeftHeader = null;

            lock (_runLock)
            {
                using (var topAggr = ImagePreprocessor.ExtractTerminalNameBandAggressive(src))
                {
                    ct.ThrowIfCancellationRequested();
                    var topResult = _ocr.Run(topAggr);
                    var topRaw = RegionLayout.JoinByRows(topResult.Regions);
                    if (topResult.Regions.Length > 0)
                        confidences.AddRange(topResult.Regions.Select(r => (double)r.Score));

                    _logger.LogInformation(
                        "Retry top-banner OCR ({Regions} regions): {Text}",
                        topResult.Regions.Length, topRaw.Replace("\n", " | "));
                    combinedText.AppendLine("--- RETRY TOP BANNER PASS ---").AppendLine(topRaw);

                    var topLines = topRaw.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                        .Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
                    recoveredBanner = _matcher.MatchTerminalAcrossLines(topLines, minScore: 65);
                }

                if (recoveredBanner is null || recoveredBanner.Score < 90)
                {
                    using var leftAggr = ImagePreprocessor.ExtractLeftPanelHeaderAggressive(src);
                    ct.ThrowIfCancellationRequested();
                    var leftResult = _ocr.Run(leftAggr);
                    var leftRaw = RegionLayout.JoinByRows(leftResult.Regions);
                    if (leftResult.Regions.Length > 0)
                        confidences.AddRange(leftResult.Regions.Select(r => (double)r.Score));

                    _logger.LogInformation(
                        "Retry left-header OCR ({Regions} regions): {Text}",
                        leftResult.Regions.Length, leftRaw.Replace("\n", " | "));
                    combinedText.AppendLine("--- RETRY LEFT HEADER PASS ---").AppendLine(leftRaw);

                    var leftLines = leftRaw.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                        .Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
                    recoveredLeftHeader = _matcher.MatchTerminalAcrossLines(leftLines, minScore: 65);
                }
            }

            var recoveredTerminal = ChooseBetter(recoveredBanner, recoveredLeftHeader);
            if (recoveredTerminal is not null)
            {
                var sourceLabel = ReferenceEquals(recoveredTerminal, recoveredBanner) ? "retry_banner" : "retry_left_header";
                submission.IdTerminal = recoveredTerminal.Terminal.Id;
                submission.TerminalDisplayName = recoveredTerminal.Terminal.DisplayName;
                submission.TerminalMatchScore = recoveredTerminal.Score;
                submission.TerminalMatchedFromOcr = recoveredTerminal.FromOcr;
                submission.TerminalMatchedField = $"{sourceLabel}:{recoveredTerminal.MatchedField}";
                CleanReviewFlagsForTerminal(submission);
                _logger.LogInformation(
                    "Retry recovered terminal: {Name} (score={Score}, source={Source})",
                    recoveredTerminal.Terminal.DisplayName, recoveredTerminal.Score, sourceLabel);
            }
            else
            {
                _logger.LogWarning(
                    "Retry FAILED to recover terminal — manual selection required.");
            }
        }

        // === RETRY 2: status + values for rows that have Status == Unknown ===
        if (submission.Prices.Any(p => p.StatusBuy == InventoryStatus.Unknown))
        {
            string retryRightRaw;
            lock (_runLock)
            {
                using var rightAggr = ImagePreprocessor.EnhanceAggressive(src, cropRightPanel: true);
                ct.ThrowIfCancellationRequested();
                var retryResult = _ocr.Run(rightAggr);
                if (retryResult.Regions.Length > 0)
                    confidences.AddRange(retryResult.Regions.Select(r => (double)r.Score));
                retryRightRaw = RegionLayout.JoinByRows(retryResult.Regions);
                _logger.LogInformation(
                    "Retry right-panel OCR ({W}x{H}, {Regions} regions): {Text}",
                    rightAggr.Width, rightAggr.Height, retryResult.Regions.Length,
                    retryRightRaw.Replace("\n", " | "));
                combinedText.AppendLine("--- RETRY RIGHT PANEL PASS ---").AppendLine(retryRightRaw);
            }

            // Re-run the parser on the aggressive OCR text. Only the recovered
            // STATUS values are merged back — commodity matches and prices from
            // the retry are NOT trusted to overwrite first-pass results because
            // the aggressive preprocessing can alter digits and letters.
            var retrySubmission = _parser.Parse(retryRightRaw, submission.SourceImage ?? "");

            var beforeUnknownCount = submission.Prices.Count(p => p.StatusBuy == InventoryStatus.Unknown);
            var recoveredCount = 0;
            foreach (var existingRow in submission.Prices)
            {
                if (existingRow.StatusBuy != InventoryStatus.Unknown) continue;
                if (existingRow.IdCommodity is null) continue;

                var match = retrySubmission.Prices
                    .FirstOrDefault(r => r.IdCommodity == existingRow.IdCommodity
                        && r.StatusBuy != InventoryStatus.Unknown);
                if (match is not null)
                {
                    _logger.LogInformation(
                        "Retry recovered status for {Commodity}: {Status} (raw={Raw})",
                        existingRow.CommodityName, match.StatusBuy, match.RawStatus);
                    existingRow.StatusBuy = match.StatusBuy;
                    existingRow.RawStatus = match.RawStatus;

                    // When the retry detected OutOfStock, InferOutOfStockState
                    // already set match.ScuBuy = 0 on the retry submission.
                    // We must propagate that to the original row too, otherwise
                    // the row ends up with Status=OutOfStock + ScuBuy=null →
                    // the validator flags it as "SCU missing" even though 0 is
                    // the only valid value for an out-of-stock commodity.
                    if (existingRow.ScuBuy is null && match.ScuBuy is not null)
                    {
                        existingRow.ScuBuy = match.ScuBuy;
                        existingRow.RawScu = match.RawScu;
                    }

                    recoveredCount++;
                }
            }
            _logger.LogInformation(
                "Retry result: recovered {Recovered}/{Before} status values; {Remaining} rows still Unknown.",
                recoveredCount, beforeUnknownCount, beforeUnknownCount - recoveredCount);
        }

        retrySw.Stop();
        _logger.LogInformation("OCR retry passes completed in {Ms}ms.", retrySw.ElapsedMilliseconds);
    }

    private static void CleanReviewFlagsForTerminal(ParsedSubmission submission)
    {
        submission.NeedsReview.RemoveAll(f =>
            f.StartsWith("terminal_not_detected", StringComparison.Ordinal) ||
            f.StartsWith("terminal_match_low_confidence", StringComparison.Ordinal));

        if (submission.TerminalMatchScore < 90)
        {
            submission.NeedsReview.Add(
                $"terminal_match_low_confidence ({submission.TerminalMatchScore:F0})");
        }
    }

    public void Dispose() => _ocr.Dispose();
}
