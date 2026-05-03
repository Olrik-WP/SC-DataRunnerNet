using DataRunner.Core.Models;

namespace DataRunner.Core.Abstractions;

/// <summary>
/// Detects "near duplicates" of an outgoing payload by combining:
///  - LOCAL : recent submissions cached in <see cref="ISubmissionHistory"/> within last N minutes
///  - REMOTE: latest known prices from GET /commodities_raw_prices for the target terminal
///
/// UEX rejects same (item, location) within 5 minutes server-side; we mirror that locally
/// to avoid wasted requests AND raise warnings on suspicious value drift.
/// </summary>
public interface IDuplicateChecker
{
    Task<DuplicateReport> CheckAsync(UexDataSubmitPayload payload, CancellationToken ct = default);
}

public sealed record DuplicateReport(
    DuplicateSeverity Worst,
    IReadOnlyList<DuplicateFinding> Findings,
    IReadOnlyList<UexCommodityRawPrice> LiveSnapshot);

public sealed record DuplicateFinding(
    int RowIndex,
    int IdCommodity,
    string CommodityLabel,
    DuplicateSeverity Severity,
    string Reason,
    double? LocalValue = null,
    double? RemoteValue = null,
    double? PercentDifference = null,
    DateTimeOffset? RemoteLastUpdate = null);

public enum DuplicateSeverity
{
    Ok,         // green: no concerns
    Info,       // grey: informational only
    Warning,    // yellow: 5-30% drift, allow with confirmation
    Block,      // red: <5min duplicate or >30% drift, REQUIRES explicit override
}
