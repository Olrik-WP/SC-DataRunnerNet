namespace DataRunner.Core.Models;

/// <summary>
/// Raw output of a single OCR engine pass over an image.
/// Used both by the OCR benchmark spike and by the production pipeline.
/// </summary>
public sealed record OcrResult(
    string EngineName,
    string Text,
    double MeanConfidence,
    long ElapsedMs);
