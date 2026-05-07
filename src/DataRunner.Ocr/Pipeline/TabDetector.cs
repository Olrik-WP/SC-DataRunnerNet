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
    /// <see cref="DecisionMargin"/> above against false positives on
    /// screenshots that aren't an SC commodity terminal at all
    /// (corrupted captures, other game UIs, etc.).
    ///
    /// Calibrated from real captures: a Seraphim Station SELL screen
    /// produced sellSat=39.2 vs buySat=19.3 — a clean 2× ratio that
    /// our previous 40-floor rejected as "both grey". 25 is the new
    /// floor; it accepts the dim active labels seen on amber-themed
    /// stations while still rejecting pure-noise images (saturation
    /// stays below 10 for non-SC content).</summary>
    private const double MinActiveSaturation = 25.0;

    /// <summary>HSV-V threshold (0..255) above which a pixel is treated
    /// as "text-bright". Below this is dark glow / panel background that
    /// would dilute the saturation average if included.</summary>
    private const int BrightnessFloor = 100;

    /// <summary>Vertical padding (in original-image px) added above and
    /// below the OCR bounding box when sampling colours. Captures the
    /// small highlight bar SC renders just above the active tab text,
    /// which is often the cleanest source of saturated pixels.</summary>
    private const int VerticalPadding = 4;

    /// <summary>Minimum HSV-V (brightness, 0..255) gap between the two
    /// labels before we commit to a result on the VALUE axis. Pyro
    /// stations render BOTH tabs in the same red hue (saturation
    /// indistinguishable, ~252 on both labels) but the active tab is
    /// noticeably BRIGHTER than the inactive one. Calibrated from the
    /// 2026-05-07 Pyro Endgame logs where the V gap stays around
    /// 30..60 units; 20 is the floor that admits the dimmer Pyro
    /// captures while staying comfortably above pixel noise.</summary>
    private const double ValueDecisionMargin = 20.0;

    /// <summary>The brighter label's mean V must reach at least this
    /// before the V-axis fallback fires. Stops the fallback from
    /// committing to a result on a corrupted/dark image where both
    /// labels are dim. Calibrated against Pyro Endgame where the
    /// active label sits around V≈210 and the inactive around
    /// V≈170.</summary>
    private const double MinActiveValue = 140.0;

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

        var buy = MeasureLabel(originalImage, buyRegion.Value, rightPanelStartX, scaleFactor);
        var sell = MeasureLabel(originalImage, sellRegion.Value, rightPanelStartX, scaleFactor);

        if (buy is null || sell is null)
            return new TabDetectionResult(TerminalTab.Unknown,
                buy?.Saturation, sell?.Saturation, "roi-too-small", DetectedPalette.Unknown);

        var palette = ClassifyPalette(buy.Value, sell.Value);

        // ---- AXIS 1: SATURATION (Stanton, Crusader, most amber stations) -------
        // Inactive label is rendered in a near-grey desaturated tint, active
        // label in the theme accent colour. Wide saturation gap → easy
        // decision. Tried first because it has the most calibration data
        // and the lowest historical false-positive rate.
        var satDiff = buy.Value.Saturation - sell.Value.Saturation;
        var maxSat = Math.Max(buy.Value.Saturation, sell.Value.Saturation);
        var satConclusive =
            maxSat >= MinActiveSaturation
            && Math.Abs(satDiff) >= DecisionMargin;

        if (satConclusive)
        {
            return new TabDetectionResult(
                satDiff > 0 ? TerminalTab.Buy : TerminalTab.Sell,
                buy.Value.Saturation,
                sell.Value.Saturation,
                UnknownReason: null,
                palette);
        }

        // ---- AXIS 2: VALUE (Pyro red theme, where saturation is identical) ------
        // Pyro renders BOTH tabs in the same saturated red — only the
        // brightness (HSV-V) differs between active (bright) and inactive
        // (dim). Trigger this fallback ONLY when saturation was
        // inconclusive; never overrides a confident saturation decision,
        // so Stanton captures behave exactly as before.
        var valDiff = buy.Value.Value - sell.Value.Value;
        var maxVal = Math.Max(buy.Value.Value, sell.Value.Value);
        var valConclusive =
            maxVal >= MinActiveValue
            && Math.Abs(valDiff) >= ValueDecisionMargin;

        if (valConclusive)
        {
            return new TabDetectionResult(
                valDiff > 0 ? TerminalTab.Buy : TerminalTab.Sell,
                buy.Value.Saturation,
                sell.Value.Saturation,
                UnknownReason: null,
                palette);
        }

        // ---- BOTH AXES INCONCLUSIVE — give up gracefully ------------------------
        // Surface the most informative reason so the log triage points
        // at WHY the decision failed (palette + which axis had the
        // tighter gap). UI fallback (manual tab pick) takes over.
        var reason = maxSat < MinActiveSaturation
            ? $"both-grey-and-similar-V (S {buy.Value.Saturation:F1}/{sell.Value.Saturation:F1}, V {buy.Value.Value:F1}/{sell.Value.Value:F1}, palette={palette})"
            : $"low-margin-on-both-axes (S {buy.Value.Saturation:F1}/{sell.Value.Saturation:F1} |diff|={Math.Abs(satDiff):F1}, V {buy.Value.Value:F1}/{sell.Value.Value:F1} |diff|={Math.Abs(valDiff):F1}, palette={palette})";

        return new TabDetectionResult(
            TerminalTab.Unknown,
            buy.Value.Saturation,
            sell.Value.Saturation,
            reason,
            palette);
    }

    /// <summary>
    /// Computes the mean HSV (hue, saturation, value) of the bright pixels
    /// inside the region. Two-axis design covers BOTH theme families seen
    /// across SC:
    ///  • Stanton/teal/amber: inactive tab is grey, active is saturated.
    ///    Discriminator = SATURATION (S).
    ///  • Pyro/red: BOTH tabs are saturated red — only brightness differs.
    ///    Discriminator = VALUE (V).
    /// Hue is returned for diagnostic palette classification only; the
    /// decision itself is hue-agnostic.
    ///
    /// Returns <c>null</c> when the sampled rectangle is too small or
    /// contains no bright pixels (caller treats that as "no decision").
    /// </summary>
    private static LabelSamples? MeasureLabel(
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

        if (origW < 4 || origH < 4) return null;

        using var roi = new Mat(originalImage, new Rect(origX, origY, origW, origH));
        using var hsv = new Mat();
        Cv2.CvtColor(roi, hsv, ColorConversionCodes.BGR2HSV);

        var hsvChannels = Cv2.Split(hsv);
        try
        {
            // Mask of "text-bright" pixels — only the rendered glyphs and
            // their close glow. Excludes panel background which would
            // pull the means down regardless of tab state.
            using var brightMask = new Mat();
            Cv2.Threshold(hsvChannels[2], brightMask, BrightnessFloor, 255, ThresholdTypes.Binary);

            var brightCount = Cv2.CountNonZero(brightMask);
            if (brightCount < 8) return null;

            // S-axis mean: discriminates grey vs colored (Stanton/teal).
            // Active label (any saturated colour): typically 130..220.
            // Inactive label (grey/white)        : typically  10.. 50.
            var meanS = Cv2.Mean(hsvChannels[1], brightMask).Val0;

            // V-axis mean: discriminates dim vs bright at the same hue
            // (Pyro/red). Active label sits ~210, inactive ~170.
            var meanV = Cv2.Mean(hsvChannels[2], brightMask).Val0;

            // Hue mean: only useful for palette classification (red
            // wraps around 0 and 180 in OpenCV's 0..179 H-channel — we
            // handle the wrap inside ClassifyPalette).
            var meanH = Cv2.Mean(hsvChannels[0], brightMask).Val0;

            return new LabelSamples(meanH, meanS, meanV);
        }
        finally
        {
            foreach (var c in hsvChannels) c.Dispose();
        }
    }

    /// <summary>
    /// Bins the two labels' mean hues into a coarse palette family for
    /// diagnostic logging. Hue is computed only on bright pixels of the
    /// label glyphs (panel background already filtered out by
    /// <see cref="MeasureLabel"/>'s brightness mask) so the result
    /// reflects the THEME ACCENT, not the panel chrome.
    ///
    /// OpenCV's H channel is 0..179 (half of the conventional 0..360),
    /// with red wrapping around both 0 and 180. We pick the palette
    /// from whichever label has the higher saturation (the active one
    /// — its hue is the cleanest sample of the theme accent).
    /// </summary>
    private static DetectedPalette ClassifyPalette(LabelSamples buy, LabelSamples sell)
    {
        // Pick the more saturated label's hue — it carries the theme
        // accent. The other one is often grey (Stanton) and would
        // contribute meaningless hue noise.
        var dominant = buy.Saturation >= sell.Saturation ? buy : sell;
        if (dominant.Saturation < 25.0) return DetectedPalette.Unknown;

        var h = dominant.Hue;
        if (h <= 10 || h >= 170) return DetectedPalette.Red;     // Pyro
        if (h is > 10 and <= 25) return DetectedPalette.Amber;   // Crusader, some faction terminals
        if (h is > 25 and <= 40) return DetectedPalette.Yellow;
        if (h is > 40 and <= 80) return DetectedPalette.Green;
        if (h is > 80 and <= 100) return DetectedPalette.Teal;   // Stanton classic
        if (h is > 100 and <= 130) return DetectedPalette.Blue;
        if (h is > 130 and < 170) return DetectedPalette.Purple;
        return DetectedPalette.Unknown;
    }

    /// <summary>Holder for the three HSV channel means measured over a
    /// label's bright pixels. Used to drive both the saturation-axis
    /// (Stanton) and value-axis (Pyro) discrimination paths in
    /// <see cref="Diagnose"/>.</summary>
    private readonly record struct LabelSamples(double Hue, double Saturation, double Value);
}

/// <summary>
/// Coarse classification of the theme accent colour observed on the
/// label glyphs. Used purely for diagnostic logging so we can tell at a
/// glance which captures fall back to the V-axis path (Pyro red) vs.
/// the classic S-axis (Stanton teal/amber). Adding a value here is a
/// safe operation — no decision logic in <see cref="TabDetector"/>
/// branches on the palette family; the dual-axis algorithm picks the
/// right axis automatically based on which gap is conclusive.
/// </summary>
public enum DetectedPalette
{
    Unknown = 0,
    Red,    // Pyro stations
    Amber,  // Crusader Industries-style HUDs
    Yellow,
    Green,
    Teal,   // Stanton classic
    Blue,
    Purple,
}

/// <summary>
/// Result of <see cref="TabDetector.Diagnose"/>. Carries the decision
/// plus the raw saturation samples and the detected theme palette so
/// callers can log or display the numbers when the detector returned
/// <see cref="TerminalTab.Unknown"/>.
/// </summary>
/// <param name="Tab">Active tab decision (Buy / Sell / Unknown).</param>
/// <param name="BuySaturation">Mean HSV-S of the BUY label, or
/// <c>null</c> when the BUY label was not found / ROI too small.</param>
/// <param name="SellSaturation">Mean HSV-S of the LOCAL MARKET VALUE
/// label, or <c>null</c> when not measurable.</param>
/// <param name="UnknownReason">Free-form reason describing why
/// <see cref="Tab"/> is <see cref="TerminalTab.Unknown"/>; <c>null</c>
/// when a decision was made.</param>
/// <param name="Palette">Coarse classification of the theme accent
/// colour (Pyro red, Stanton teal, Crusader amber, …). Always
/// populated even on success — useful to spot palette-related
/// regressions in batch log analysis.</param>
public readonly record struct TabDetectionResult(
    TerminalTab Tab,
    double? BuySaturation,
    double? SellSaturation,
    string? UnknownReason,
    DetectedPalette Palette = DetectedPalette.Unknown);
