using DataRunner.App.ViewModels;
using DataRunner.Core.Abstractions;
using DataRunner.Core.Models;
using Microsoft.Extensions.Logging;

namespace DataRunner.App.Services;

/// <summary>
/// Builds a "smart-split" batch plan from a flat list of validated inbox
/// items, eliminating the (id_terminal, id_commodity, tab) duplicates that
/// would otherwise trigger UEX's <c>duplicated_report</c> rejection (5-min
/// server-side guard, see <see href="https://uexcorp.space/api/documentation/id/post_data_submit/"/>).
///
/// IMPORTANT: BUY and SELL of the same (terminal, commodity) are NOT
/// considered duplicates by the planner — UEX stores `price_buy` and
/// `price_sell` in separate columns on their backend, so submitting
/// `(Endgame, Aluminum, BUY)` and later `(Endgame, Aluminum, SELL)` is
/// expected to land as two independent measurements. Collapsing them here
/// would silently drop legitimate user data the OCR pipeline correctly
/// captured from two distinct terminal tabs.
///
/// Algorithm:
///   1. Group items by <see cref="ParsedSubmission.IdTerminal"/>.
///   2. Inside each terminal group, sort items by <see cref="InboxItem.AddedAt"/>
///      DESCENDING so the most recent capture wins for any commodity that
///      appears more than once across the screenshots ("latest wins" — same
///      semantic as the historical merge feature).
///   3. Walk the rows once, tracking which (commodity_id, tab) pairs have
///      already been "claimed". The first occurrence wins (i.e. the most
///      recent thanks to the sort), every subsequent occurrence is recorded
///      as <see cref="DedupedRow"/> with a human-readable reason that the
///      preview dialog surfaces 1:1 to the user.
///   4. Build one <see cref="PlannedSubmission"/> per source screenshot. Each
///      submission carries ONLY its claimed commodities, and points at its own
///      <see cref="InboxItem.ImagePath"/> for the screenshot attachment so UEX
///      gets the visual evidence it needs (even past the 90-day evaluation).
/// </summary>
public interface IBatchPlanner
{
    BatchPlan Plan(IReadOnlyList<InboxItem> validatedItems);
}

public sealed class BatchPlanner : IBatchPlanner
{
    private readonly ICatalogProvider _catalog;
    private readonly ILogger<BatchPlanner> _logger;

    public BatchPlanner(ICatalogProvider catalog, ILogger<BatchPlanner> logger)
    {
        _catalog = catalog;
        _logger = logger;
    }

    public BatchPlan Plan(IReadOnlyList<InboxItem> validatedItems)
    {
        var planned = new List<PlannedSubmission>();
        var deduped = new List<DedupedRow>();

        // The QueuePosition assignment is used by the preview UI to label each
        // screenshot with a stable "#N" tag (matching the inbox card badge so
        // the user can cross-reference). Compute it once up-front so we don't
        // depend on collection order shifts later.
        var positions = new Dictionary<InboxItem, int>();
        for (var i = 0; i < validatedItems.Count; i++)
            positions[validatedItems[i]] = validatedItems[i].QueuePosition > 0
                ? validatedItems[i].QueuePosition
                : i + 1;

        // Items without an OCR submission can't participate in the dedup pass
        // (no commodities to claim). They become an empty PlannedSubmission so
        // the preview surfaces them and the user can see what's wrong.
        foreach (var group in validatedItems.GroupBy(i => i.Submission?.IdTerminal ?? -1))
        {
            // Within a terminal group: sort newest-first. Ties broken on
            // QueuePosition so the picture order shown in the inbox is
            // deterministic when timestamps collide (eg. bulk import).
            var ordered = group
                .OrderByDescending(i => i.AddedAt)
                .ThenByDescending(i => positions.TryGetValue(i, out var p) ? p : 0)
                .ToList();

            // Track which (commodity, tab) pairs have been claimed inside
            // THIS terminal group. Two screenshots of terminal X can both
            // carry "Aluminum" without being duplicates if one is the BUY
            // tab and the other is the SELL tab — UEX stores those in
            // separate columns and expects two independent submissions.
            // Cross-terminal collisions are irrelevant — the UEX rule is
            // per (terminal, commodity, tab) tuple, so terminal A's
            // "Aluminum" BUY and terminal B's "Aluminum" BUY are also
            // independent submissions.
            var claimed = new HashSet<(int commodityId, TerminalTab tab)>();

            foreach (var item in ordered)
            {
                var rows = new List<ParsedPriceRow>();
                var submission = item.Submission;
                if (submission is not null)
                {
                    var tab = submission.Tab;
                    foreach (var row in submission.Prices)
                    {
                        if (row.IdCommodity is not int cid)
                        {
                            // Commodities without a resolved id are kept on
                            // the originating screenshot — the validator will
                            // gate them later, but they're not duplicate
                            // candidates so let them ride.
                            rows.Add(row);
                            continue;
                        }

                        // Unknown tab is a no-op for dedup: the validator
                        // will block the submission downstream anyway, but
                        // we still want to surface the row in the preview
                        // so the user understands why this screen will fail.
                        var key = (cid, tab);

                        if (claimed.Add(key))
                        {
                            rows.Add(row);
                        }
                        else
                        {
                            // Find the screenshot that already claimed this
                            // (commodity, tab) pair to give the user a clear
                            // "moved to screen #X" trail. Walk the previously-
                            // processed entries in reverse insertion order;
                            // the most recent winner is what we want to point
                            // at. We require the SAME tab so a SELL screen
                            // never gets pointed at a BUY winner (that would
                            // be misleading — they don't actually collide).
                            var winner = planned
                                .Where(p => p.IdTerminal == (submission.IdTerminal ?? 0)
                                            && p.Tab == tab)
                                .LastOrDefault(p => p.Rows.Any(r => r.IdCommodity == cid));

                            deduped.Add(new DedupedRow(
                                IdCommodity: cid,
                                CommodityLabel: LabelFor(cid, row),
                                IdTerminal: submission.IdTerminal,
                                TerminalLabel: submission.TerminalDisplayName ?? item.TerminalLabel ?? "?",
                                Tab: tab,
                                FoundOnItem: item,
                                FoundOnQueuePosition: positions.TryGetValue(item, out var fp) ? fp : 0,
                                AssignedToItem: winner?.SourceItem,
                                AssignedToQueuePosition: winner?.QueuePosition ?? 0,
                                Reason: winner is not null
                                    ? $"Already claimed by screenshot #{winner.QueuePosition} on the same {TabLabel(tab)} tab (newer capture wins)."
                                    : $"Already claimed earlier in this batch on the {TabLabel(tab)} tab."));
                        }
                    }
                }

                planned.Add(new PlannedSubmission(
                    SourceItem: item,
                    QueuePosition: positions.TryGetValue(item, out var pos) ? pos : 0,
                    IdTerminal: submission?.IdTerminal ?? 0,
                    TerminalLabel: submission?.TerminalDisplayName ?? item.TerminalLabel ?? "(no terminal)",
                    Tab: submission?.Tab ?? TerminalTab.Unknown,
                    Rows: rows,
                    OriginalRowCount: submission?.Prices.Count ?? 0));
            }
        }

        // Restore the natural inbox order in the output for predictable UI.
        var orderedPlan = planned
            .OrderBy(p => p.QueuePosition)
            .ToList();

        var totalSent = orderedPlan.Sum(p => p.Rows.Count(r => r.IdCommodity is not null));
        var totalDeduped = deduped.Count;

        _logger.LogInformation(
            "Batch plan: {Items} items -> {Submissions} submissions, {Sent} commodities sent, {Deduped} deduplicated.",
            validatedItems.Count, orderedPlan.Count, totalSent, totalDeduped);

        return new BatchPlan(orderedPlan, deduped, totalSent, totalDeduped);
    }

    private string LabelFor(int idCommodity, ParsedPriceRow row)
    {
        var c = _catalog.GetCommodity(idCommodity);
        if (c is not null) return $"{c.Name} ({c.Code})";
        if (!string.IsNullOrWhiteSpace(row.CommodityName))
            return $"{row.CommodityName} (#{idCommodity})";
        return $"#{idCommodity}";
    }

    /// <summary>Human-readable tab label for the dedup reason text.</summary>
    private static string TabLabel(TerminalTab tab) => tab switch
    {
        TerminalTab.Buy => "BUY",
        TerminalTab.Sell => "SELL",
        _ => "(unknown)",
    };
}

/// <summary>
/// Output of <see cref="IBatchPlanner.Plan"/>. Carries the ORDERED list of
/// per-screenshot submissions and the (parallel, informational) list of rows
/// that were dropped during dedup so the preview dialog can show the user
/// exactly what's about to happen.
/// </summary>
public sealed record BatchPlan(
    IReadOnlyList<PlannedSubmission> Submissions,
    IReadOnlyList<DedupedRow> DedupedRows,
    int TotalCommoditiesSent,
    int TotalCommoditiesDeduped);

/// <summary>
/// One outgoing UEX submission inside a batch. Always backed by exactly ONE
/// <see cref="InboxItem"/> (its original screenshot) so the wire payload's
/// <c>screenshot</c> field can be populated with the correct image.
/// <see cref="Tab"/> is carried at the submission level (a /data_submit POST
/// is always single-tab — BUY or SELL) so the dedup pass can keep BUY and
/// SELL of the same (terminal, commodity) as separate measurements.
/// </summary>
public sealed record PlannedSubmission(
    InboxItem SourceItem,
    int QueuePosition,
    int IdTerminal,
    string TerminalLabel,
    TerminalTab Tab,
    IReadOnlyList<ParsedPriceRow> Rows,
    int OriginalRowCount);

/// <summary>
/// Informational record for the preview dialog: a row that was present on
/// <see cref="FoundOnItem"/> but won't be sent because a newer screenshot
/// (<see cref="AssignedToItem"/>) already carries it on the SAME terminal
/// tab. <see cref="Reason"/> is a one-line explanation that appears verbatim
/// in the dialog's "Reason" column. <see cref="Tab"/> identifies the side
/// (BUY / SELL) involved so the user understands a SELL row was never going
/// to collide with a BUY row of the same commodity.
/// </summary>
public sealed record DedupedRow(
    int IdCommodity,
    string CommodityLabel,
    int? IdTerminal,
    string TerminalLabel,
    TerminalTab Tab,
    InboxItem FoundOnItem,
    int FoundOnQueuePosition,
    InboxItem? AssignedToItem,
    int AssignedToQueuePosition,
    string Reason);
