using DataRunner.Core.Models;

namespace DataRunner.Core.Abstractions;

/// <summary>
/// End-to-end OCR pipeline for a single Star Citizen commodity terminal screenshot.
/// Implementations:
///   1. Extract terminal name (top banner + left header passes)
///   2. Extract commodity rows from the right shop panel
///   3. Resolve names to UEX catalog ids (fuzzy)
///   4. Build a wire-shaped UexDataSubmitPayload (with local _meta block)
/// </summary>
public interface IOcrPipeline
{
    Task<OcrPipelineResult> RunAsync(string imagePath, CancellationToken ct = default);
}

/// <summary>
/// Asynchronous factory for the OCR pipeline. The first call may take several seconds
/// (download/load PaddleOCR models, allocate native runtime). Subsequent calls return
/// the cached singleton instance.
/// </summary>
public interface IOcrPipelineFactory
{
    Task<IOcrPipeline> GetAsync(CancellationToken ct = default);
    bool IsReady { get; }
}

public sealed record OcrPipelineResult(
    OcrResult Ocr,
    ParsedSubmission Submission,
    UexDataSubmitPayload Payload);
