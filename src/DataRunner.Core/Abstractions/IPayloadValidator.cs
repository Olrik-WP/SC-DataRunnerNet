using DataRunner.Core.Models;

namespace DataRunner.Core.Abstractions;

/// <summary>
/// Static (offline) validation of an outgoing UEX payload. No network calls.
/// Checks shape, exclusivity rules, ID existence in local catalog, value ranges.
/// </summary>
public interface IPayloadValidator
{
    ValidationReport Validate(UexDataSubmitPayload payload);
}

public sealed record ValidationReport(
    bool IsBlocking,
    IReadOnlyList<ValidationIssue> Issues)
{
    public bool HasErrors => Issues.Any(i => i.Severity == ValidationSeverity.Error);
    public bool HasWarnings => Issues.Any(i => i.Severity == ValidationSeverity.Warning);
}

public sealed record ValidationIssue(
    ValidationSeverity Severity,
    string Code,
    string Message,
    int? RowIndex = null);

public enum ValidationSeverity
{
    Info,
    Warning,
    Error,
}
