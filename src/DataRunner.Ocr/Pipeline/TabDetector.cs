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
    /// as "text-bright" for the SATURATION-axis mean. Below this is dark
    /// glow / panel background that would dilute the saturation average
    /// if included. Calibrated so Stanton greyish inactive labels (V≈
    /// 180-220) and saturated active labels (V≈180-230) both register,
    /// while the dark panel chrome (V&lt;50) is excluded.</summary>
    private const int BrightnessFloor = 100;

    /// <summary>HSV-V threshold (0..255) used SPECIFICALLY for the
    /// VALUE-axis mean. Tighter than <see cref="BrightnessFloor"/> on
    /// purpose: on Pyro red-themed terminals BOTH tabs (active and
    /// inactive) sit on a saturated red BACKGROUND fill, with the
    /// active fill at V≈130-180 and the inactive at V≈80-110. The
    /// looser <see cref="BrightnessFloor"/> would leak the active
    /// background into the V mean and pull it close to the inactive
    /// label's glyph-only V (~150), collapsing the V gap from ≈40 to
    /// ≈2 — exactly what the 2026-05-07 Pyro Endgame logs show
    /// (buyV=151.5 vs sellV=149.2 on a SELL-active capture). 150
    /// keeps the bright glyph cores of BOTH tabs (active glyphs ≈
    /// 200, inactive glyphs ≈ 150-170) while excluding the panel-
    /// chrome and inactive-background contributions. Stanton is
    /// unaffected because the S-axis decides first there.</summary>
    private const int VAxisBrightnessFloor = 150;

    /// <summary>Vertical padding (in original-image px) added above and
    /// below the OCR bounding box when sampling colours. Captures the
    /// small highlight bar SC renders just above the active tab text,
    /// which is often the cleanest source of saturated pixels.</summary>
    private const int VerticalPadding = 4;

    /// <summary>Horizontal padding (in original-image px) added on
    /// either side of the OCR bounding box when sampling colours.
    /// Critical for the dim INACTIVE tab on Pyro red-themed terminals:
    /// PaddleOCR returns a very tight bounding box around the dim "BUY"
    /// glyphs (~6-8 px wide on the upscaled mat) which collapses to
    /// 3-4 px after dividing by scaleFactor, falling under the
    /// <see cref="MinRoiPixels"/> floor and triggering
    /// <c>roi-too-small</c>. Reduced from 8 to 4 on 2026-05-07 (after
    /// the +8 setting was shown to dilute the V-axis mean by including
    /// too much tab-background area on Pyro): 4 still pads the
    /// degenerate 3-4 px BUY ROI up to 11-12 px (well above the
    /// <see cref="MinRoiPixels"/> floor) without bleeding far into the
    /// surrounding tab fill. The <see cref="VAxisBrightnessFloor"/>
    /// glyph-core mask is the second-line defence against any residual
    /// background contamination.</summary>
    private const int HorizontalPadding = 4;

    /// <summary>Minimum width/height (in original-image px) the sampled
    /// ROI must reach before <see cref="MeasureLabel"/> commits to a
    /// reading. Below this floor the bright-pixel count is too small
    /// to produce a stable HSV mean. Lowered from 4 to 2 because the
    /// new <see cref="HorizontalPadding"/> guarantees ≥ 16 px wide ROI
    /// even on degenerate OCR boxes — keeping a floor at all is just a
    /// last-resort guard against pathological mat slicing.</summary>
    private const int MinRoiPixels = 2;

    /// <summary>Minimum HSV-V (brightness, 0..255) gap on the value axis
    /// for NON-RED palettes (Stanton teal, Crusader amber, etc.) — kept
    /// strict because on those palettes the S-axis decides first with a
    /// huge margin, and a V-axis fallback only fires on degenerate
    /// captures where any small V difference is more likely noise than
    /// signal. Stanton glyphs (white/grey vs. teal/amber) all sit at
    /// V≈200, so the genuine V gap is usually below 5 — we'd rather
    /// return Unknown than commit to a coin-flip.</summary>
    private const double ValueDecisionMarginGeneric = 20.0;

    /// <summary>Minimum bright-pixel-RATIO gap between the two labels
    /// before we commit to a Pyro red-palette decision. Pyro renders
    /// the active tab as a SOLID FILLED rectangle of the theme accent
    /// (orange/red, V≈150-180) with DARKER text inside (V≈140-160),
    /// while the inactive tab is just dim text on the dark panel
    /// chrome. As a result mean-V over the OCR bbox is unreliable —
    /// active text is darker than inactive text in absolute V terms,
    /// so the V-mean ordering can flip the wrong way on SELL-active
    /// captures (observed on 2026-05-07 Pyro Endgame: buyV=171 (dim
    /// inactive text) vs sellV=161 (bright bg + dark active text),
    /// classifier picked Buy when SELL was active).
    ///
    /// The ratio of pixels above <see cref="BrightnessFloor"/> over
    /// total ROI area is a more discriminating signal: the active tab
    /// fills most of the bbox with above-floor pixels (bright bg +
    /// most of the text), while the inactive tab only has the text
    /// glyph cores above the floor. Real captures land around
    /// activeRatio≈0.7..0.9 vs inactiveRatio≈0.2..0.4, a wide gap
    /// that 0.15 captures while staying above pixel-noise on
    /// Stanton-on-Red-misclassification scenarios. Combined with the
    /// <see cref="MinActiveBrightRatio"/> floor (one of the labels
    /// MUST be ≥ 0.35 — i.e. clearly filled) this rejects tab-bar
    /// captures where neither side is highlighted (corrupt screenshot
    /// or game state).</summary>
    private const double BrightRatioDecisionMarginPyro = 0.15;

    /// <summary>The more-filled of the two labels must reach at least
    /// this bright-pixel ratio for the Pyro V-axis fallback to fire.
    /// Stops the detector from committing to a decision when both
    /// labels are dim (eg. tab bar partially off-screen, transition
    /// frame, corrupted capture).</summary>
    private const double MinActiveBrightRatio = 0.35;

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

        // Geometric fallback for the BUY label: when MeasureLabel could
        // not produce a usable sample from the OCR-reported bbox (typical
        // on Pyro SELL-tab captures where the dim BUY glyphs make the
        // detection box collapse to a few px) but the SELL label landed
        // a clean reading, we know exactly where BUY lives in original-
        // image coordinates: same Y band as SELL, somewhere between the
        // right panel's left edge and SELL's left edge. Sampling that
        // band directly works because the brightness mask in
        // MeasureRectInOriginal filters out panel chrome and only the
        // BUY glyph pixels survive into the HSV averages. This keeps the
        // detector functional for the common Pyro pattern instead of
        // surfacing roi-too-small.
        if (buy is null && sell is not null)
        {
            buy = GeometricBuyFallback(originalImage, sellRegion.Value, rightPanelStartX, scaleFactor);
        }

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
                palette,
                buy.Value.Value,
                sell.Value.Value,
                buy.Value.BrightRatio,
                sell.Value.BrightRatio);
        }

        // ---- AXIS 2: PALETTE-SPECIFIC FALLBACK ----------------------------------
        // S-axis was inconclusive. Pick a secondary discriminator
        // appropriate for the detected palette family. Each branch
        // returns Unknown rather than fall through to the OTHER
        // branch's metric — the metrics aren't interchangeable and
        // mixing them would produce silent misclassifications when
        // both signals point in opposite directions on the same
        // capture.
        var valDiff = buy.Value.Value - sell.Value.Value;
        var ratioDiff = buy.Value.BrightRatio - sell.Value.BrightRatio;

        if (palette == DetectedPalette.Red)
        {
            // PYRO RED: BOTH tabs are saturated red, V-mean is
            // unreliable (active tab has bright bg with DARKER text
            // than the inactive tab's text — V-mean ordering can flip
            // wrong on SELL-active captures). The discriminator is
            // BRIGHT-PIXEL RATIO: active tab has a solid filled
            // rectangle covering most of the bbox (high ratio),
            // inactive tab is just text glyphs on dark chrome (low
            // ratio). 2026-05-07 calibration: active≈0.7-0.9,
            // inactive≈0.2-0.4, so a 0.15 margin captures the signal
            // robustly.
            var maxRatio = Math.Max(buy.Value.BrightRatio, sell.Value.BrightRatio);
            var ratioConclusive =
                maxRatio >= MinActiveBrightRatio
                && Math.Abs(ratioDiff) >= BrightRatioDecisionMarginPyro;

            if (ratioConclusive)
            {
                return new TabDetectionResult(
                    ratioDiff > 0 ? TerminalTab.Buy : TerminalTab.Sell,
                    buy.Value.Saturation,
                    sell.Value.Saturation,
                    UnknownReason: null,
                    palette,
                    buy.Value.Value,
                    sell.Value.Value,
                    buy.Value.BrightRatio,
                    sell.Value.BrightRatio);
            }
        }
        else
        {
            // STANTON/TEAL/AMBER/OTHER: text glyphs on dark chrome on
            // BOTH tabs (no filled bg), so the bright-ratio signal is
            // weak (both labels ≈ 0.2-0.3). Use V-mean instead — works
            // reliably on these themes when the S-axis happened to
            // miss (eg. amber stations where the dim active label
            // produces a tighter S gap).
            var maxVal = Math.Max(buy.Value.Value, sell.Value.Value);
            var valConclusive =
                maxVal >= MinActiveValue
                && Math.Abs(valDiff) >= ValueDecisionMarginGeneric;

            if (valConclusive)
            {
                return new TabDetectionResult(
                    valDiff > 0 ? TerminalTab.Buy : TerminalTab.Sell,
                    buy.Value.Saturation,
                    sell.Value.Saturation,
                    UnknownReason: null,
                    palette,
                    buy.Value.Value,
                    sell.Value.Value,
                    buy.Value.BrightRatio,
                    sell.Value.BrightRatio);
            }
        }

        // ---- BOTH AXES INCONCLUSIVE — give up gracefully ------------------------
        // Surface the most informative reason so the log triage points
        // at WHY the decision failed (palette + measurement values).
        // UI fallback (manual tab pick) takes over.
        var reason = maxSat < MinActiveSaturation
            ? $"both-grey (S {buy.Value.Saturation:F1}/{sell.Value.Saturation:F1}, V {buy.Value.Value:F1}/{sell.Value.Value:F1}, ratio {buy.Value.BrightRatio:F2}/{sell.Value.BrightRatio:F2}, palette={palette})"
            : $"low-margin (S {buy.Value.Saturation:F1}/{sell.Value.Saturation:F1} |d|={Math.Abs(satDiff):F1}, V {buy.Value.Value:F1}/{sell.Value.Value:F1} |d|={Math.Abs(valDiff):F1}, ratio {buy.Value.BrightRatio:F2}/{sell.Value.BrightRatio:F2} |d|={Math.Abs(ratioDiff):F2}, palette={palette})";

        return new TabDetectionResult(
            TerminalTab.Unknown,
            buy.Value.Saturation,
            sell.Value.Saturation,
            reason,
            palette,
            buy.Value.Value,
            sell.Value.Value,
            buy.Value.BrightRatio,
            sell.Value.BrightRatio);
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

        // Vertical padding: catches the small accent bar SC renders
        // immediately above/below the active tab text.
        origY = Math.Max(0, origY - VerticalPadding);
        origH = Math.Min(originalImage.Height - origY, origH + 2 * VerticalPadding);

        // Horizontal padding: rescues the dim "BUY" label on Pyro
        // SELL-tab captures (and any other case where the OCR bbox is
        // too tight to sample). Clamped to [rightPanelStartX,
        // imageWidth] so we never reach into the LEFT panel content.
        var paddedX = origX - HorizontalPadding;
        origX = Math.Clamp(paddedX, rightPanelStartX, Math.Max(rightPanelStartX, originalImage.Width - 1));
        origW = Math.Min(originalImage.Width - origX, origW + 2 * HorizontalPadding);

        return MeasureRectInOriginal(originalImage, origX, origY, origW, origH);
    }

    /// <summary>
    /// Geometric fallback used when <see cref="MeasureLabel"/> could
    /// not produce a usable sample for the BUY label. SC always renders
    /// the BUY tab to the LEFT of the LOCAL MARKET VALUE tab on the
    /// same Y band; given the SELL bounding box we therefore know the
    /// BUY label is somewhere inside <c>[rightPanelStartX..sellX]</c>
    /// at the same vertical position. We sample that whole strip and
    /// rely on <see cref="MeasureRectInOriginal"/>'s brightness mask
    /// to filter out the dark panel chrome — only the BUY glyphs
    /// (which are the only bright pixels in that strip on a
    /// commodity-terminal screenshot) contribute to the HSV averages.
    /// </summary>
    private static LabelSamples? GeometricBuyFallback(
        Mat originalImage,
        PaddleOcrResultRegion sellRegion,
        int rightPanelStartX,
        double scaleFactor)
    {
        var sellBounds = sellRegion.Rect.BoundingRect();
        var sellOrigX = (int)(sellBounds.X / scaleFactor) + rightPanelStartX;
        var sellOrigY = (int)(sellBounds.Y / scaleFactor);
        var sellOrigH = (int)Math.Ceiling(sellBounds.Height / scaleFactor);

        // Y band: same as SELL plus a small padding on each side.
        var origY = Math.Max(0, sellOrigY - VerticalPadding);
        var origH = Math.Min(originalImage.Height - origY, sellOrigH + 2 * VerticalPadding);

        // X band: from right panel's left edge up to SELL. We trim a
        // few pixels off the right edge so the SELL glyphs themselves
        // are not included in the BUY sample.
        var origX = rightPanelStartX;
        var origW = sellOrigX - rightPanelStartX - 2;

        // Sanity: the strip must be at least 16 px wide to contain a
        // 3-letter label. A narrower strip means the SELL bbox started
        // unusually close to the panel edge — bail out rather than
        // sample chrome.
        if (origW < 16 || origH < 8) return null;

        return MeasureRectInOriginal(originalImage, origX, origY, origW, origH);
    }

    /// <summary>
    /// Shared HSV-sampling primitive used by both <see cref="MeasureLabel"/>
    /// (OCR-driven) and <see cref="GeometricBuyFallback"/> (geometry-driven).
    /// Builds a brightness mask, then averages H/S/V over the bright pixels.
    /// </summary>
    private static LabelSamples? MeasureRectInOriginal(
        Mat originalImage,
        int origX,
        int origY,
        int origW,
        int origH)
    {
        if (origW < MinRoiPixels || origH < MinRoiPixels) return null;
        if (origX < 0 || origY < 0
            || origX + origW > originalImage.Width
            || origY + origH > originalImage.Height) return null;

        using var roi = new Mat(originalImage, new Rect(origX, origY, origW, origH));
        using var hsv = new Mat();
        Cv2.CvtColor(roi, hsv, ColorConversionCodes.BGR2HSV);

        var hsvChannels = Cv2.Split(hsv);
        Mat? glyphCoreMask = null;
        try
        {
            // PRIMARY MASK: "text-bright" pixels — glyphs and their
            // close glow, panel chrome below V=100 excluded. Used for
            // S-axis and Hue means. Stanton's grey-vs-coloured
            // discrimination has always worked at this floor.
            using var brightMask = new Mat();
            Cv2.Threshold(hsvChannels[2], brightMask, BrightnessFloor, 255, ThresholdTypes.Binary);

            var brightCount = Cv2.CountNonZero(brightMask);
            if (brightCount < 8) return null;

            // S-axis mean: discriminates grey vs coloured (Stanton/teal).
            // Active label (any saturated colour): typically 130..220.
            // Inactive label (grey/white)        : typically  10.. 50.
            var meanS = Cv2.Mean(hsvChannels[1], brightMask).Val0;

            // SECONDARY MASK for V-axis: tighter floor isolates GLYPH
            // CORES from the saturated tab-fill background that Pyro
            // renders behind both labels. Without this the V means
            // collapse to ≈150 on both labels (background-dominated)
            // and the V-axis decision can't tell active from inactive.
            // Stanton is unaffected because both glyph types easily
            // exceed V=150 and the S-axis decides first anyway.
            glyphCoreMask = new Mat();
            Cv2.Threshold(hsvChannels[2], glyphCoreMask, VAxisBrightnessFloor, 255, ThresholdTypes.Binary);
            var glyphCoreCount = Cv2.CountNonZero(glyphCoreMask);

            // V-axis mean: discriminates dim vs bright at the same hue
            // (Pyro red). Active label sits ~200 in glyph cores,
            // inactive ~150. Falls back to the looser brightMask V mean
            // when the tighter mask is too sparse to give a stable
            // average — protects degenerate captures from a synthetic
            // V≈0 reading.
            var meanV = glyphCoreCount >= 4
                ? Cv2.Mean(hsvChannels[2], glyphCoreMask).Val0
                : Cv2.Mean(hsvChannels[2], brightMask).Val0;

            // Hue mean: only useful for palette classification (red
            // wraps around 0 and 180 in OpenCV's 0..179 H-channel — we
            // handle the wrap inside ClassifyPalette).
            var meanH = Cv2.Mean(hsvChannels[0], brightMask).Val0;

            // Bright ratio: proportion of ROI pixels above the loose
            // BrightnessFloor. PRIMARY discriminator for Pyro red,
            // where the active tab is a solid filled rectangle (most
            // of the bbox above the floor) and the inactive is just
            // text glyphs on dark chrome (small fraction above).
            var totalPixels = origW * origH;
            var brightRatio = totalPixels > 0
                ? (double)brightCount / totalPixels
                : 0.0;

            return new LabelSamples(meanH, meanS, meanV, brightRatio);
        }
        finally
        {
            glyphCoreMask?.Dispose();
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

    /// <summary>Holder for the per-label measurements:
    /// <list type="bullet">
    /// <item><c>Hue</c>/<c>Saturation</c>/<c>Value</c> — HSV channel
    /// means over the bright-pixel mask (Stanton/Pyro generic
    /// discrimination paths).</item>
    /// <item><c>BrightRatio</c> — fraction of the ROI (0..1) whose V
    /// channel is above <see cref="BrightnessFloor"/>. Used as the
    /// PRIMARY discriminator on Pyro red-themed terminals where the
    /// active tab has a solid filled background that takes most of
    /// the bbox, while the inactive tab only renders text on dark
    /// chrome.</item>
    /// </list>
    /// </summary>
    private readonly record struct LabelSamples(double Hue, double Saturation, double Value, double BrightRatio);
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
/// plus the raw measurement samples and the detected theme palette so
/// callers can log or display the numbers — useful both for triage
/// when the detector returned <see cref="TerminalTab.Unknown"/> AND for
/// calibration of palette-specific thresholds against fresh captures.
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
/// <param name="BuyValue">Mean HSV-V of the BUY label over the
/// glyph-core mask. Diagnostic-only on success; one of the inputs
/// to the V-axis decision on non-Red palettes.</param>
/// <param name="SellValue">Mean HSV-V of the LOCAL MARKET VALUE
/// label. Diagnostic-only on success; one of the inputs to the
/// V-axis decision on non-Red palettes.</param>
/// <param name="BuyBrightRatio">Fraction of BUY ROI pixels above
/// the bright-pixel floor. Primary discriminator on Pyro Red
/// (active tab has filled bg ≈ 0.7-0.9 vs inactive text-only ≈
/// 0.2-0.4). Diagnostic-only on non-Red palettes.</param>
/// <param name="SellBrightRatio">Fraction of LMV ROI pixels above
/// the bright-pixel floor. See <see cref="BuyBrightRatio"/>.</param>
public readonly record struct TabDetectionResult(
    TerminalTab Tab,
    double? BuySaturation,
    double? SellSaturation,
    string? UnknownReason,
    DetectedPalette Palette = DetectedPalette.Unknown,
    double? BuyValue = null,
    double? SellValue = null,
    double? BuyBrightRatio = null,
    double? SellBrightRatio = null);
