using System.Diagnostics;
using DataRunner.Core.Models;
using TesseractOCR;
using TesseractOCR.Enums;

namespace DataRunner.OcrSpike.Engines;

public sealed class TesseractEngine : IOcrEngine
{
    public string Name => "Tesseract";

    private readonly Engine _engine;

    public TesseractEngine(string tessdataDir, string language = "eng")
    {
        _engine = new Engine(tessdataDir, language, EngineMode.Default);
    }

    public Task<OcrResult> RecognizeAsync(string imagePath, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        using var img = TesseractOCR.Pix.Image.LoadFromFile(imagePath);
        using var page = _engine.Process(img);

        var text = page.Text ?? string.Empty;
        var confidence = page.MeanConfidence;

        sw.Stop();

        return Task.FromResult(new OcrResult(
            EngineName: Name,
            Text: text,
            MeanConfidence: confidence,
            ElapsedMs: sw.ElapsedMilliseconds));
    }

    public void Dispose() => _engine.Dispose();
}
