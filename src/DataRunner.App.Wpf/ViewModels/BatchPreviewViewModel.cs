using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DataRunner.App.Services;

namespace DataRunner.App.ViewModels;

/// <summary>
/// Hydrates the <see cref="Views.Dialogs.BatchPreviewDialog"/> from a
/// <see cref="BatchPlan"/>. The dialog is mandatory (always shown before
/// any /data_submit POST in the batch flow) so the user has an opportunity
/// to spot mistakes the smart-split algorithm could plausibly make.
///
/// One <see cref="BatchPreviewRow"/> per commodity occurrence — kept rows
/// AND deduped rows share the same table so the user sees the full picture
/// in a single glance ("Commodity X is on screenshots #1 and #4 → it will
/// be sent on #4 because it's newer").
/// </summary>
public sealed partial class BatchPreviewViewModel : ObservableObject
{
    public ObservableCollection<BatchPreviewRow> Rows { get; } = new();

    [ObservableProperty] private int _submissionsCount;
    [ObservableProperty] private int _commoditiesSent;
    [ObservableProperty] private int _commoditiesDeduped;

    /// <summary>One-line stat shown in the dialog footer next to the action buttons.</summary>
    public string Summary => $"{SubmissionsCount} submissions · {CommoditiesSent} commodities sent · {CommoditiesDeduped} deduplicated";

    partial void OnSubmissionsCountChanged(int value) => OnPropertyChanged(nameof(Summary));
    partial void OnCommoditiesSentChanged(int value) => OnPropertyChanged(nameof(Summary));
    partial void OnCommoditiesDedupedChanged(int value) => OnPropertyChanged(nameof(Summary));

    public BatchPreviewViewModel(BatchPlan plan)
    {
        SubmissionsCount = plan.Submissions.Count;
        CommoditiesSent = plan.TotalCommoditiesSent;
        CommoditiesDeduped = plan.TotalCommoditiesDeduped;

        // Materialise every "kept" row first so the user sees what will
        // actually be sent before scrolling into the deduplicated tail.
        foreach (var s in plan.Submissions)
        {
            foreach (var r in s.Rows)
            {
                if (r.IdCommodity is not int cid) continue;
                Rows.Add(new BatchPreviewRow(
                    CommodityLabel: r.CommodityName ?? $"#{cid}",
                    TerminalLabel: s.TerminalLabel,
                    FoundOnLabel: $"#{s.QueuePosition}",
                    SentOnLabel: $"#{s.QueuePosition}",
                    Reason: "Kept",
                    IsKept: true));
            }
        }

        // Then the deduped ones — same DataGrid so the user reads a single
        // table per commodity.
        foreach (var d in plan.DedupedRows)
        {
            Rows.Add(new BatchPreviewRow(
                CommodityLabel: d.CommodityLabel,
                TerminalLabel: d.TerminalLabel,
                FoundOnLabel: $"#{d.FoundOnQueuePosition}",
                SentOnLabel: d.AssignedToQueuePosition > 0
                    ? $"#{d.AssignedToQueuePosition}"
                    : "(skipped)",
                Reason: d.Reason,
                IsKept: false));
        }
    }
}

/// <summary>
/// One row in the BatchPreviewDialog grid. Kept rows surface in green-tinted
/// styling, deduped rows in red — the binding lives in the dialog XAML so
/// the row record stays a plain data carrier.
/// </summary>
public sealed record BatchPreviewRow(
    string CommodityLabel,
    string TerminalLabel,
    string FoundOnLabel,
    string SentOnLabel,
    string Reason,
    bool IsKept);
