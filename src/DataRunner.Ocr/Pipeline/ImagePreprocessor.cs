using OpenCvSharp;

namespace DataRunner.Ocr.Pipeline;

/// <summary>
/// Lightweight preprocessing aimed at boosting OCR on Star Citizen UI.
/// Provides distinct crops:
/// - RIGHT panel  -> SHOP INVENTORY rows (commodities, prices, SCU, status, container sizes)
/// - TOP band     -> terminal name banner ("EVERUS HARBOR", "MIC-L2 LONG FOREST STATION", ...)
/// - LEFT header  -> terminal name fallback under "YOUR INVENTORIES"
///
/// LEFT panel BODY = player inventory and is NEVER fed to OCR (privacy + risk of submitting
/// the player's own goods as shop inventory).
///
/// All methods return a NEW Mat. Caller MUST dispose.
/// </summary>
public static class ImagePreprocessor
{
    private const double RightPanelStartFraction = 0.55;
    private const double TopBandHeightFraction = 0.15;
    private const double LeftHeaderWidthFraction = 0.55;
    private const double LeftHeaderHeightFraction = 0.30;

    public static Mat Enhance(Mat src, bool upscale = true, bool cropRightPanel = true)
    {
        if (src.Empty()) throw new ArgumentException("Source image is empty.");

        Mat working = cropRightPanel ? CropRightPanel(src) : src.Clone();
        try
        {
            ApplyClahe(working);

            if (upscale && (working.Width < 1920 || working.Height < 1080))
            {
                var dst = new Mat();
                Cv2.Resize(working, dst, new Size(working.Width * 2, working.Height * 2),
                    interpolation: InterpolationFlags.Lanczos4);
                working.Dispose();
                return dst;
            }

            return working;
        }
        catch
        {
            working.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Aggressive variant of <see cref="Enhance"/>, used as a SECOND-CHANCE
    /// preprocessing when the default pass left holes (eg. an OCR row whose
    /// "MAX INVENTORY" status was not detected). Trades runtime + minor
    /// distortion for a real shot at recovering missed text:
    ///   - tighter CLAHE (clipLimit 4.0, 4×4 tiles) → more local contrast
    ///   - unsharp mask → crisper edges (good for the SC glow-heavy font)
    ///   - upscale ×3 instead of ×2 → small status labels become big enough
    ///     for PaddleOCR's recognizer to read confidently
    ///
    /// Caller MUST dispose the returned Mat.
    /// </summary>
    public static Mat EnhanceAggressive(Mat src, bool cropRightPanel = true)
    {
        if (src.Empty()) throw new ArgumentException("Source image is empty.");

        Mat working = cropRightPanel ? CropRightPanel(src) : src.Clone();
        try
        {
            ApplyClaheAggressive(working);
            ApplyUnsharpMask(working);

            var dst = new Mat();
            Cv2.Resize(working, dst, new Size(working.Width * 3, working.Height * 3),
                interpolation: InterpolationFlags.Lanczos4);
            working.Dispose();
            return dst;
        }
        catch
        {
            working.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Aggressive variant for the top banner (terminal name) — same recipe as
    /// <see cref="EnhanceAggressive"/> but operating on the top band only.
    /// </summary>
    public static Mat ExtractTerminalNameBandAggressive(Mat src)
    {
        if (src.Empty()) throw new ArgumentException("Source image is empty.");

        var height = (int)(src.Height * TopBandHeightFraction);
        if (height < 20) height = Math.Min(60, src.Height);
        var roi = new Rect(0, 0, src.Width, height);
        var band = new Mat(src, roi).Clone();
        try
        {
            ApplyClaheAggressive(band);
            ApplyUnsharpMask(band);
            var dst = new Mat();
            Cv2.Resize(band, dst, new Size(band.Width * 3, band.Height * 3),
                interpolation: InterpolationFlags.Lanczos4);
            band.Dispose();
            return dst;
        }
        catch
        {
            band.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Aggressive variant for the left panel header (terminal name fallback).
    /// </summary>
    public static Mat ExtractLeftPanelHeaderAggressive(Mat src)
    {
        if (src.Empty()) throw new ArgumentException("Source image is empty.");

        var width = (int)(src.Width * LeftHeaderWidthFraction);
        var height = (int)(src.Height * LeftHeaderHeightFraction);
        if (width < 100) width = Math.Min(src.Width, 100);
        if (height < 60) height = Math.Min(src.Height, 60);

        var roi = new Rect(0, 0, width, height);
        var crop = new Mat(src, roi).Clone();
        try
        {
            ApplyClaheAggressive(crop);
            ApplyUnsharpMask(crop);
            var dst = new Mat();
            Cv2.Resize(crop, dst, new Size(crop.Width * 3, crop.Height * 3),
                interpolation: InterpolationFlags.Lanczos4);
            crop.Dispose();
            return dst;
        }
        catch
        {
            crop.Dispose();
            throw;
        }
    }

    public static Mat ExtractLeftPanelHeader(Mat src)
    {
        if (src.Empty()) throw new ArgumentException("Source image is empty.");

        var width = (int)(src.Width * LeftHeaderWidthFraction);
        var height = (int)(src.Height * LeftHeaderHeightFraction);
        if (width < 100) width = Math.Min(src.Width, 100);
        if (height < 60) height = Math.Min(src.Height, 60);

        var roi = new Rect(0, 0, width, height);
        var crop = new Mat(src, roi).Clone();
        try
        {
            ApplyClahe(crop);
            var dst = new Mat();
            Cv2.Resize(crop, dst, new Size(crop.Width * 2, crop.Height * 2),
                interpolation: InterpolationFlags.Lanczos4);
            crop.Dispose();
            return dst;
        }
        catch
        {
            crop.Dispose();
            throw;
        }
    }

    public static Mat ExtractTerminalNameBand(Mat src)
    {
        if (src.Empty()) throw new ArgumentException("Source image is empty.");

        var height = (int)(src.Height * TopBandHeightFraction);
        if (height < 20) height = Math.Min(60, src.Height);
        var roi = new Rect(0, 0, src.Width, height);
        var band = new Mat(src, roi).Clone();
        try
        {
            ApplyClahe(band);
            var dst = new Mat();
            Cv2.Resize(band, dst, new Size(band.Width * 2, band.Height * 2),
                interpolation: InterpolationFlags.Lanczos4);
            band.Dispose();
            return dst;
        }
        catch
        {
            band.Dispose();
            throw;
        }
    }

    private static void ApplyClahe(Mat working)
    {
        ApplyClaheCore(working, clipLimit: 2.5, tileSize: 8);
    }

    /// <summary>Tighter CLAHE for the aggressive retry pass: higher clip limit
    /// + smaller tiles boost local contrast on dim status labels that the
    /// default pass missed.</summary>
    private static void ApplyClaheAggressive(Mat working)
    {
        ApplyClaheCore(working, clipLimit: 4.0, tileSize: 4);
    }

    private static void ApplyClaheCore(Mat working, double clipLimit, int tileSize)
    {
        using var lab = new Mat();
        Cv2.CvtColor(working, lab, ColorConversionCodes.BGR2Lab);

        var labChannels = Cv2.Split(lab);
        try
        {
            using var clahe = Cv2.CreateCLAHE(clipLimit: clipLimit, tileGridSize: new Size(tileSize, tileSize));
            clahe.Apply(labChannels[0], labChannels[0]);

            using var merged = new Mat();
            Cv2.Merge(labChannels, merged);
            Cv2.CvtColor(merged, working, ColorConversionCodes.Lab2BGR);
        }
        finally
        {
            foreach (var c in labChannels) c.Dispose();
        }
    }

    /// <summary>Unsharp mask: GaussianBlur(src) + AddWeighted(src, 1+amount,
    /// blur, -amount). Accentuates edges, particularly useful on the SC font
    /// which has soft anti-aliased glow that confuses OCR recognizers.</summary>
    private static void ApplyUnsharpMask(Mat working)
    {
        using var blur = new Mat();
        Cv2.GaussianBlur(working, blur, new Size(0, 0), sigmaX: 1.5);
        Cv2.AddWeighted(working, 1.6, blur, -0.6, 0, working);
    }

    private static Mat CropRightPanel(Mat src)
    {
        var startX = (int)(src.Width * RightPanelStartFraction);
        var width = src.Width - startX;
        var roi = new Rect(startX, 0, width, src.Height);
        return new Mat(src, roi).Clone();
    }

    /// <summary>
    /// Detects the visual row bands of the commodity panel (one band per
    /// commodity card) using morphological closing + horizontal projection.
    ///
    /// Algorithm:
    ///   1. Binarize the panel (Otsu).
    ///   2. Morphologically close with a wide horizontal kernel — text
    ///      fragments inside a card get merged into a single solid band per
    ///      card. Inter-card gaps (much wider than intra-card spacing) stay
    ///      open because the kernel is sized to the typical intra-card gap.
    ///   3. Compute row density on the closed image. Density is now bimodal:
    ///      ~80%+ inside cards, ~0% in inter-card gaps. The threshold becomes
    ///      trivial.
    ///
    /// This is much more robust than projecting raw pixel density (which we
    /// tried first), where in-card text varies wildly between rows
    /// (commodity name = sparse, cargo-size pills = dense) and you have to
    /// paper over the variance with delicate thresholds that break easily.
    ///
    /// Returns an empty list if the detection is unreliable (band count
    /// outside 2..8 or all bands too small) so the caller can fall back to
    /// single-pass OCR. The reason is logged.
    /// </summary>
    public static List<Rect> DetectRowBands(Mat panel)
        => DetectRowBands(panel, out _);

    /// <summary>
    /// Variant of <see cref="DetectRowBands(Mat)"/> that also exposes a brief
    /// diagnostic about why detection succeeded or failed.
    ///
    /// Strategy: instead of binarizing and counting bright pixels (which
    /// proved fragile because Otsu can mis-classify the SC panel's gradient
    /// background as "bright"), we use the per-row GRAYSCALE MEAN. Inter-card
    /// gaps in SC are noticeably darker than card content (no text, no pill
    /// borders, no glow). We compute the panel's dynamic range (min..max of
    /// row means) and treat the bottom 30% of that range as gap candidates.
    /// This adapts to whatever absolute brightness the SC scene happens to
    /// have, so it works on every screenshot regardless of HDR / time-of-day
    /// in the game world.
    /// </summary>
    public static List<Rect> DetectRowBands(Mat panel, out string diagnostic)
    {
        diagnostic = "ok";
        if (panel.Empty() || panel.Rows < 60)
        {
            diagnostic = $"panel too small (rows={panel.Rows})";
            return new List<Rect>();
        }

        using var gray = new Mat();
        Cv2.CvtColor(panel, gray, ColorConversionCodes.BGR2GRAY);

        using var blur = new Mat();
        Cv2.GaussianBlur(gray, blur, new Size(5, 5), 0);

        // Per-row average brightness (0..255). No binarization, so we don't
        // depend on Otsu picking a "right" threshold for this particular
        // panel — we just look at whose row is darker than the others.
        var rowMean = new double[blur.Rows];
        for (var y = 0; y < blur.Rows; y++)
        {
            using var row = blur.Row(y);
            rowMean[y] = row.Mean()[0];
        }

        var smoothed = MovingAverage(rowMean, window: 11);

        // Adaptive gap threshold based on the panel's own dynamic range.
        // This lets the algorithm work whether the panel renders at 60..80 grey
        // (dim scene) or 100..160 grey (bright scene): the BOTTOM 30% of the
        // observed brightness is treated as gap candidates.
        var panelMin = smoothed.Min();
        var panelMax = smoothed.Max();
        if (panelMax - panelMin < 5)
        {
            diagnostic = $"panel has no contrast (min={panelMin:F1}, max={panelMax:F1})";
            return new List<Rect>();
        }
        var gapBrightnessThreshold = panelMin + (panelMax - panelMin) * 0.30;

        var minGapHeight = Math.Max(20, panel.Rows / 60);
        var minBandHeight = Math.Max(80, panel.Rows / 14);

        var gaps = new List<(int Start, int End)>();
        int? gapStart = null;
        for (var y = 0; y < smoothed.Length; y++)
        {
            var isGap = smoothed[y] < gapBrightnessThreshold;
            if (isGap)
            {
                gapStart ??= y;
            }
            else if (gapStart is not null)
            {
                var width = y - gapStart.Value;
                if (width >= minGapHeight)
                {
                    gaps.Add((gapStart.Value, y));
                }
                gapStart = null;
            }
        }
        if (gapStart is not null && smoothed.Length - gapStart.Value >= minGapHeight)
        {
            gaps.Add((gapStart.Value, smoothed.Length));
        }

        var bands = new List<Rect>();
        var prevEnd = 0;
        foreach (var (gStart, gEnd) in gaps)
        {
            var height = gStart - prevEnd;
            if (height >= minBandHeight)
            {
                bands.Add(new Rect(0, prevEnd, panel.Width, height));
            }
            prevEnd = gEnd;
        }
        var lastHeight = panel.Rows - prevEnd;
        if (lastHeight >= minBandHeight)
        {
            bands.Add(new Rect(0, prevEnd, panel.Width, lastHeight));
        }

        if (bands.Count is < 2 or > 8)
        {
            diagnostic = $"unreliable band count ({bands.Count}, found {gaps.Count} gaps, " +
                         $"panel brightness {panelMin:F1}..{panelMax:F1}, gap threshold={gapBrightnessThreshold:F1})";
            return new List<Rect>();
        }

        return bands;
    }

    private static double[] MovingAverage(double[] values, int window)
    {
        var result = new double[values.Length];
        var half = window / 2;
        for (var i = 0; i < values.Length; i++)
        {
            var start = Math.Max(0, i - half);
            var end = Math.Min(values.Length, i + half + 1);
            var sum = 0.0;
            for (var j = start; j < end; j++) sum += values[j];
            result[i] = sum / (end - start);
        }
        return result;
    }
}
