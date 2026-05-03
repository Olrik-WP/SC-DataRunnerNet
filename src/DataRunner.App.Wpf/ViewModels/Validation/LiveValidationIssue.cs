namespace DataRunner.App.ViewModels.Validation;

/// <summary>
/// One issue surfaced by the live UI validator that runs continuously while
/// the user edits a submission. Distinct from <c>Core.Abstractions.IPayloadValidator</c>
/// which validates a FINISHED payload — this one is incremental and triggers
/// on every keystroke / row change so the user sees errors before clicking Send.
/// </summary>
public sealed class LiveValidationIssue
{
    public required LiveValidationSeverity Severity { get; init; }

    /// <summary>Stable machine code, e.g. "scu_empty", "match_score_low".</summary>
    public required string Code { get; init; }

    /// <summary>Human-readable, ready for display in the issue panel.</summary>
    public required string Message { get; init; }

    /// <summary>0-based index of the row this issue is about, or null for header-level issues.</summary>
    public int? RowIndex { get; init; }
}

/// <summary>
/// Severity of a <see cref="LiveValidationIssue"/>.
///   - <see cref="Error"/>   : MUST be fixed before Send. Send button is disabled.
///   - <see cref="Warning"/> : SHOULD be reviewed but doesn't block submission.
///   - <see cref="Info"/>    : Purely advisory.
/// </summary>
public enum LiveValidationSeverity
{
    Info,
    Warning,
    Error,
}
