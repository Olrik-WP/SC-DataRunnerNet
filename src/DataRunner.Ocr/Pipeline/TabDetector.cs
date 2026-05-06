using DataRunner.Core.Models;
using OpenCvSharp;
using Sdcb.PaddleOCR;

namespace DataRunner.Ocr.Pipeline;

/// <summary>
/// Determines which tab — BUY or LOCAL MARKET VALUE (sell) — is currently
/// active on a Star Citizen commodity terminal screenshot, by comparing
/// the visual saturation of the two tab labels detected by OCR.
///
/// Why this is needed: BOTH tab labels are always rendered on the tab bar
/// — one is highlighted (active), the other is dimmed (inactive). A pure
/// text-based heuristic therefore cannot tell them apart; it must rely on
/// the visual highlight.
///
/// Why saturation instead of a fixed hue (eg. teal): SC uses different
/// accent colours per faction / terminal type. Most stations render the
/// active tab in teal/cyan, but several outposts and faction terminals
/// render it in amber/orange (Crusader Industries-style HUDs, certain
/// Pyro stations, etc). A teal-specific metric would silently invert the
/// decision on those terminals — orange's red dominance would flip a
/// "(G+B)/2 - R" score negative. The inactive label, by contrast, is
/// always rendered in a near-grey desaturated tint regardless of theme.
/// We therefore key off the HSV saturation channel, which is high for
/// any saturated colour (cyan, teal, amber, orange, green, …) and low
/// only for whitish/grey tints.
///
/// Algorithm:
///   1. Locate OCR regions whose text fuzzy-matches "BUY" and
///      "LOCAL MARKET VALUE" (the two tab labels).
///   2. Map each region's bounding box back to the original-source-image
///      coordinate space (the OCR was run on the upscaled, cropped right
///      panel — see <see cref="ImagePreprocessor.Enhance"/>).
///   3. Compute the mean HSV saturation on the bright pixels of each
///      region. Higher → more saturated (active); near-zero → grey
///      (inactive). This is hue-agnostic.
///   4. The label with the noticeably higher saturation is reported as
///      the active tab. If the gap between the two is below a small
///      margin we return Unknown so the caller can fall back to the
///      text-based heuristic without committing to a wrong guess.
/// </summary>
public static class TabDetector
{
    /// <summary>Minimum saturation gap (HSV S-channel, 0..255) between
    /// the two tabs before we commit to a result. Real captures
    /// typically produce a 100+ unit gap on classic stations
    /// (saturated cyan/teal vs. near-grey). Pyro Gateway and a few
    /// other amber-themed stations produce a tighter gap because the
    /// inactive label still picks up some warm tint from the panel
    /// background — caused a real-world misclassification on
    /// 2026-05-05 (six SELL captures defaulted to BUY because the gap
    /// fell below the previous 25-unit floor). 12 is the new floor;
    /// it stays well above pure-noise (≤ 5) but accepts the tighter
    /// gaps observed on amber-themed terminals. The
    /// <see cref="MinActiveSaturation"/> floor below provides a
    /// secondary safety net so we still return Unknown when both
    /// labels look greyish (eg. a non-SC screenshot accidentally
    /// imported).</summary>
    private const double DecisionMargin = 12.0;

    /// <summary>The brighter of the two tab labels must reach at
    /// least this much saturation before we commit to a result. If
    /// both labels are below this floor, neither is meaningfully
    /// "highlighted" and we return Unknown — protects the smaller
    /// margin above against false positives on screenshots that
    /// aren't an SC commodity terminal at all (corrupted captures,
    /// other game UIs, etc.).</summary>
    private const double MinActiveSaturation = 40.0;

    /// <summary>HSV-V threshold (0..255) above which a pixel is treated
    /// as "text-bright". Below this is dark glow / panel background that
    /// would dilute the saturation average if included.</summary>
    private const int BrightnessFloor = 100;

    /// <summary>Vertical padding (in original-image px) added above and
    /// below the OCR bounding box when sampling colours. Captures the
    /// small highlight bar SC renders just above the active tab text,
    /// which is often the cleanest source of saturated pixels.</summary>
    private const int VerticalPadding = 4;

    /// <summary>
    /// Detects the active tab from the right-panel OCR regions. Returns
    /// <see cref="TerminalTab.Unknown"/> when one or both tab labels were
    /// not detected by OCR, or when the colour gap between the two
    /// labels is too small to commit to a result.
    /// </summary>
    /// <param name="originalImage">The unmodified BGR source screenshot
    /// (NOT the CLAHE-enhanced preprocessed Mat — CLAHE shifts saturation
    /// and would distort the cyan-vs-grey signal).</param>
    /// <param name="rightPanelRegions">The OCR regions returned from the
    /// right-panel pass.</param>
    /// <param name="rightPanelStartX">X offset (in original image px) at
    /// which the right panel starts in <paramref name="originalImage"/>.
    /// Use <see cref="ImagePreprocessor.GetRightPanelStartX"/>.</param>
    /// <param name="scaleFactor">Upscale factor applied by
    /// <see cref="ImagePreprocessor.Enhance"/> to the cropped panel before
    /// OCR (typically 2.0).</param>
    public static TerminalTab DetectActiveTab(
        Mat originalImage,
        IReadOnlyList<PaddleOcrResultRegion> rightPanelRegions,
        int rightPanelStartX,
        double scaleFactor)
        => Diagnose(originalImage, rightPanelRegions, rightPanelStartX, scaleFactor).Tab;

    /// <summary>
    /// Diagnostic-rich variant. Returns the same decision as
    /// <see cref="DetectActiveTab"/> together with the raw saturation
    /// values measured for both labels, so callers can log the
    /// numbers and triage cases where the detector returned Unknown
    /// (eg. amber-themed Pyro stations sitting just under the
    /// decision margin).
    /// </summary>
    public static TabDetectionResult Diagnose(
        Mat originalImage,
        IReadOnlyList<PaddleOcrResultRegion> rightPanelRegions,
        int rightPanelStartX,
        double scaleFactor)
    {
        if (originalImage is null || originalImage.Empty())
            return new TabDetectionResult(TerminalTab.Unknown, null, null, "no-image");
        if (rightPanelRegions is null || rightPanelRegions.Count == 0)
            return new TabDetectionResult(TerminalTab.Unknown, null, null, "no-regions");
        if (scaleFactor <= 0)
            return new TabDetectionResult(TerminalTab.Unknown, null, null, "bad-scale");

        PaddleOcrResultRegion? buyRegion = null;
        PaddleOcrResultRegion? sellRegion = null;

        for (var i = 0; i < rightPanelRegions.Count; i++)
        {
            var r = rightPanelRegions[i];
            var t = r.Text?.Trim().ToUpperInvariant() ?? "";
            if (t.Length == 0) continue;

            // "LOCAL MARKET VALUE" is unique on the panel — no other
            // label contains that 3-word phrase. PartialRatio absorbs
            // OCR fragments where the recogniser splits the label or
            // appends a stray suffix.
            if (sellRegion is null
                && t.Length >= 5
                && FuzzySharp.Fuzz.PartialRatio(t, "LOCAL MARKET VALUE") >= 80)
            {
                sellRegion = r;
                continue;
            }

            // "BUY" must be matched with a tighter constraint
            // (length <= 5) because the substring "BUY" appears inside
            // other tokens (eg. multi-region merges, stray icon glyphs).
            // The active "BUY" label is rendered alone on its tab so
            // it lands as a short, isolated region.
            if (buyRegion is null
                && t.Length <= 5
                && FuzzySharp.Fuzz.WeightedRatio(t, "BUY") >= 90)
            {
                buyRegion = r;
            }
        }

        if (buyRegion is null && sellRegion is null)
            return new TabDetectionResult(TerminalTab.Unknown, null, null, "no-labels");
        if (buyRegion is null)
            return new TabDetectionResult(TerminalTab.Unknown, null, null, "missing-buy-label");
        if (sellRegion is null)
            return new TabDetectionResult(TerminalTab.Unknown, null, null, "missing-sell-label");

        var buySat = MeasureSaturation(originalImage, buyRegion.Value, rightPanelStartX, scaleFactor);
        var sellSat = MeasureSaturation(originalImage, sellRegion.Value, rightPanelStartX, scaleFactor);

        if (buySat < 0 || sellSat < 0)
            return new TabDetectionResult(TerminalTab.Unknown,
                buySat < 0 ? (double?)null : buySat,
                sellSat < 0 ? (double?)null : sellSat,
                "roi-too-small");

        var diff = buySat - sellSat;
        var maxSat = Math.Max(buySat, sellSat);

        // Both labels are nearly grey — no meaningful highlight at
        // all. Return Unknown so we don't commit to a false positive
        // off pure pixel noise (eg. a corrupted or non-SC capture).
        if (maxSat < MinActiveSaturation)
            return new TabDetectionResult(TerminalTab.Unknown, buySat, sellSat,
                $"both-grey ({buySat:F1} vs {sellSat:F1}, max<{MinActiveSaturation:F0})");

        if (Math.Abs(diff) < DecisionMargin)
            return new TabDetectionResult(TerminalTab.Unknown, buySat, sellSat,
                $"low-margin ({buySat:F1} vs {sellSat:F1}, |diff|<{DecisionMargin:F0})");

        return new TabDetectionResult(
            diff > 0 ? TerminalTab.Buy : TerminalTab.Sell,
            buySat,
            sellSat,
            null);
    }

    /// <summary>
    /// Computes the mean HSV saturation of the bright pixels inside the
    /// region. Hue-agnostic by design — works for teal/cyan terminals
    /// (the typical SC accent) AND for amber/orange terminals (some
    /// faction stations) AND for any other saturated theme colour. The
    /// inactive tab is always rendered grey/white, so a saturated active
    /// label is reliably distinguishable regardless of its specific hue.
    ///
    /// Returns <c>-1</c> when the sampled rectangle is too small or
    /// contains no bright pixels (caller treats that as "no decision").
    /// </summary>
    private static double MeasureSaturation(
        Mat originalImage,
        PaddleOcrResultRegion region,
        int rightPanelStartX,
        double scaleFactor)
    {
        var bounds = region.Rect.BoundingRect();

        // OCR ran on the upscaled, cropped right panel. Reverse both
        // transforms to get back to original-image pixel coordinates.
        var origX = (int)(bounds.X / scaleFactor) + rightPanelStartX;
        var origY = (int)(bounds.Y / scaleFactor);
        var origW = (int)Math.Ceiling(bounds.Width / scaleFactor);
        var origH = (int)Math.Ceiling(bounds.Height / scaleFactor);

        origY = Math.Max(0, origY - VerticalPadding);
        origH = Math.Min(originalImage.Height - origY, origH + 2 * VerticalPadding);
        origX = Math.Clamp(origX, 0, Math.Max(0, originalImage.Width - 1));
        origW = Math.Min(originalImage.Width - origX, origW);

        if (origW < 4 || origH < 4) return -1;

        using var roi = new Mat(originalImage, new Rect(origX, origY, origW, origH));
        using var hsv = new Mat();
        Cv2.CvtColor(roi, hsv, ColorConversionCodes.BGR2HSV);

        var hsvChannels = Cv2.Split(hsv);
        try
        {
            // Mask of "text-bright" pixels — only the rendered glyphs and
            // their close glow. Excludes panel background which would
            // pull the mean saturation down regardless of tab state.
            using var brightMask = new Mat();
            Cv2.Threshold(hsvChannels[2], brightMask, BrightnessFloor, 255, ThresholdTypes.Binary);

            var brightCount = Cv2.CountNonZero(brightMask);
            if (brightCount < 8) return -1;

            // Mean of the S-channel over the bright-pixel mask.
            // Active label (any saturated colour): typically 130..220.
            // Inactive label (grey/white)        : typically  10.. 50.
            return Cv2.Mean(hsvChannels[1], brightMask).Val0;
        }
        finally
        {
            foreach (var c in hsvChannels) c.Dispose();
        }
    }
}

/// <summary>
/// Result of <see cref="TabDetector.Diagnose"/>. Carries the decision
/// plus the raw saturation samples so callers can log or display the
/// numbers when the detector returned <see cref="TerminalTab.Unknown"/>.
/// </summary>
/// <param name="Tab">Active tab decision (Buy / Sell / Unknown).</param>
/// <param name="BuySaturation">Mean HSV-S of the BUY label, or
/// <c>null</c> when the BUY label was not found / ROI too small.</param>
/// <param name="SellSaturation">Mean HSV-S of the LOCAL MARKET VALUE
/// label, or <c>null</c> when not measurable.</param>
/// <param name="UnknownReason">Free-form reason describing why
/// <see cref="Tab"/> is <see cref="TerminalTab.Unknown"/>; <c>null</c>
/// when a decision was made.</param>
public readonly record struct TabDetectionResult(
    TerminalTab Tab,
    double? BuySaturation,
    double? SellSaturation,
    string? UnknownReason);
