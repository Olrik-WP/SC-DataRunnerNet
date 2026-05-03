using DataRunner.Core.Models;

namespace DataRunner.Core.Abstractions;

/// <summary>
/// Persistent log of every UEX submission attempt (success or failure).
/// Used by the History view and the local duplicate-prevention guard.
/// </summary>
public interface ISubmissionHistory
{
    Task InitializeAsync(CancellationToken ct = default);

    Task<long> RecordAsync(SubmissionRecord record, CancellationToken ct = default);

    Task<IReadOnlyList<SubmissionRecord>> GetRecentByTerminalAsync(
        int idTerminal,
        TimeSpan window,
        CancellationToken ct = default);

    Task<IReadOnlyList<SubmissionRecord>> GetAllAsync(
        int? limit = 200,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the case-insensitive set of <c>SourceImage</c> filenames that have been
    /// submitted successfully (production OR test, configurable by <paramref name="productionOnly"/>).
    /// Used by the watcher to skip files already accepted by UEX during re-scans, so the user
    /// doesn't keep re-OCRing screenshots they have already shipped.
    /// </summary>
    Task<HashSet<string>> GetSubmittedSourceImagesAsync(bool productionOnly = true, CancellationToken ct = default);
}

public sealed class SubmissionRecord
{
    public long? Id { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
    public int IdTerminal { get; set; }
    public string? TerminalDisplayName { get; set; }
    public bool IsProduction { get; set; }
    public bool Ok { get; set; }
    public int HttpStatusCode { get; set; }
    public string? ApiStatus { get; set; }
    public string? ApiMessage { get; set; }

    /// <summary>
    /// Filename of the PRIMARY screenshot — the one actually attached to the
    /// `screenshot` field of the UEX payload (UEX accepts only one image per
    /// submission). Kept for backward compatibility with rows pre-dating the
    /// merge feature; new rows also populate <see cref="SourceImages"/>.
    /// </summary>
    public string? SourceImage { get; set; }

    /// <summary>
    /// All screenshot filenames represented by this submission (≥ 1). For a
    /// regular single-shot submission this is just <c>[SourceImage]</c>. For a
    /// merged submission it is the full list of source files so that:
    ///   - the post-send delete-after-submit can wipe every file from disk
    ///   - the watcher rescan can skip every file (not just the primary)
    /// </summary>
    public List<string> SourceImages { get; set; } = new();

    public string RequestJson { get; set; } = "";
    public string ResponseJson { get; set; } = "";
    public List<int> SubmittedCommodityIds { get; set; } = new();
}
