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

        // PaddleOcrAll.Run is NOT re-entrant; serialise per-instance.
        lock (_runLock)
        {
            using (var topBand = ImagePreprocessor.ExtractTerminalNameBand(src))
            {
                ct.ThrowIfCancellationRequested();
                var topResult = _ocr.Run(topBand);
                var topRaw = topResult.Text ?? string.Empty;
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
                var leftRaw = leftResult.Text ?? string.Empty;
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
                var ocrResult = _ocr.Run(preprocessed);
                rightRaw = ocrResult.Text ?? string.Empty;
                if (ocrResult.Regions.Length > 0)
                    confidences.AddRange(ocrResult.Regions.Select(r => (double)r.Score));
                combinedText.AppendLine("--- RIGHT PANEL PASS ---").AppendLine(rightRaw);
            }
        }

        var preferredTerminalMatch = ChooseBetter(bannerMatch, leftHeaderMatch);

        var submission = _parser.Parse(rightRaw, Path.GetFileName(imagePath));

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
