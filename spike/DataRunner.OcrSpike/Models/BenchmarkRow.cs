namespace DataRunner.OcrSpike.Models;

public sealed record BenchmarkRow(
    string Image,
    string Engine,
    double Cer,
    double Wer,
    double MeanConfidence,
    long ElapsedMs,
    string RawText);
