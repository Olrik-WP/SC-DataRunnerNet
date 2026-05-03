using System.Diagnostics;
using DataRunner.Core.Models;
using OpenCvSharp;
using Sdcb.PaddleOCR;
using Sdcb.PaddleOCR.Models;
using Sdcb.PaddleOCR.Models.Online;

namespace DataRunner.OcrSpike.Engines;

public sealed class PaddleOcrEngine : IOcrEngine
{
    public string Name => "PaddleOCR";

    private readonly PaddleOcrAll _ocr;

    private PaddleOcrEngine(PaddleOcrAll ocr) => _ocr = ocr;

    public static async Task<PaddleOcrEngine> CreateAsync(CancellationToken ct = default)
    {
        FullOcrModel model = await OnlineFullModels.EnglishV4.DownloadAsync(ct);
        var ocr = new PaddleOcrAll(model)
        {
            AllowRotateDetection = false,
            Enable180Classification = false,
        };
        return new PaddleOcrEngine(ocr);
    }

    public Task<OcrResult> RecognizeAsync(string imagePath, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        using var src = Cv2.ImRead(imagePath, ImreadModes.Color);
        if (src.Empty())
        {
            throw new InvalidOperationException($"Cannot read image: {imagePath}");
        }

        var result = _ocr.Run(src);

        var text = result.Text ?? string.Empty;
        var confidence = result.Regions.Length > 0
            ? result.Regions.Average(r => (double)r.Score)
            : 0.0;

        sw.Stop();

        return Task.FromResult(new OcrResult(
            EngineName: Name,
            Text: text,
            MeanConfidence: confidence,
            ElapsedMs: sw.ElapsedMilliseconds));
    }

    public void Dispose() => _ocr.Dispose();
}
