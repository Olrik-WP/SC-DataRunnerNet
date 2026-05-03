using DataRunner.Core.Models;

namespace DataRunner.OcrSpike.Engines;

public interface IOcrEngine : IDisposable
{
    string Name { get; }
    Task<OcrResult> RecognizeAsync(string imagePath, CancellationToken ct = default);
}
