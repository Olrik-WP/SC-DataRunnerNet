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
        using var lab = new Mat();
        Cv2.CvtColor(working, lab, ColorConversionCodes.BGR2Lab);

        var labChannels = Cv2.Split(lab);
        try
        {
            using var clahe = Cv2.CreateCLAHE(clipLimit: 2.5, tileGridSize: new Size(8, 8));
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

    private static Mat CropRightPanel(Mat src)
    {
        var startX = (int)(src.Width * RightPanelStartFraction);
        var width = src.Width - startX;
        var roi = new Rect(startX, 0, width, src.Height);
        return new Mat(src, roi).Clone();
    }
}
