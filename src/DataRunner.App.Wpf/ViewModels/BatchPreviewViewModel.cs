using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using DataRunner.App.Services;
using DataRunner.Core.Abstractions;
using DataRunner.Core.Models;
using DataRunner.UexClient;

namespace DataRunner.App.ViewModels;

/// <summary>
/// Hydrates the <see cref="Views.Dialogs.BatchPreviewDialog"/> from a
/// <see cref="BatchPlan"/>. The dialog is mandatory (always shown before
/// any /data_submit POST in the batch flow) so the user has an opportunity
/// to spot mistakes the smart-split algorithm could plausibly make.
///
/// The view model exposes three views of the batch:
///   1. <see cref="Rows"/> — the smart-split table (1 row per commodity
///      occurrence, kept and deduped together).
///   2. <see cref="Submissions"/> — 1 entry per outgoing POST, with the
///      EXACT JSON wire body that will be sent (so the user can see what
///      UEX is going to receive, screenshot bytes redacted to a "[…N base64
///      chars…]" marker so the JSON stays readable).
///   3. The "send context" pills (Mode, Branch, Attach screenshots, Delete
///      after submit, Throttle) computed from the current preferences
///      snapshot so the user knows exactly what's about to happen with the
///      images before clicking Send all.
/// </summary>
public sealed partial class BatchPreviewViewModel : ObservableObject
{
    public ObservableCollection<BatchPreviewRow> Rows { get; } = new();
    public ObservableCollection<PlannedSubmissionPreview> Submissions { get; } = new();

    [ObservableProperty] private int _submissionsCount;
    [ObservableProperty] private int _commoditiesSent;
    [ObservableProperty] private int _commoditiesDeduped;

    /// <summary>
    /// Currently-selected submission in the per-payload tab. Drives the JSON
    /// preview pane. Defaults to the first submission so the user lands on a
    /// non-empty preview when they switch to the JSON tab.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedSubmissionJson))]
    [NotifyPropertyChangedFor(nameof(SelectedSubmissionScreenshotInfo))]
    private PlannedSubmissionPreview? _selectedSubmission;

    public string SelectedSubmissionJson => SelectedSubmission?.PreviewJson ?? "";

    /// <summary>
    /// One-line summary of the current submission's screenshot attachment
    /// (file name + base64 size + on-disk size) shown above the JSON viewer
    /// so the user knows "this POST carries IMG_1234.jpg as a 4.2 MB base64
    /// blob in the `screenshot` field" without having to scroll into the
    /// massive base64 string.
    /// </summary>
    public string SelectedSubmissionScreenshotInfo => SelectedSubmission?.ScreenshotInfo ?? "";

    /// <summary>One-line stat shown in the dialog footer next to the action buttons.</summary>
    public string Summary => $"{SubmissionsCount} submissions · {CommoditiesSent} commodities sent · {CommoditiesDeduped} deduplicated";

    // ---- Send-context pills (snapshot of the prefs at batch start) ----

    /// <summary>"PRODUCTION (live to UEX)" / "TEST (recorded only)" / "Mixed"
    /// when items override the global default.</summary>
    public string ModeLabel { get; }

    /// <summary>True for the production tint (red pill), false for test (olive pill).</summary>
    public bool IsProductionMode { get; }

    /// <summary>True when at least one item overrides the global mode → mixed pill.</summary>
    public bool IsMixedMode { get; }

    /// <summary>"LIVE" / "PTU" / "Mixed".</summary>
    public string BranchLabel { get; }
    public bool HasPtuItems { get; }
    public bool IsMixedBranch { get; }

    /// <summary>"Attach screenshots: ON" / "OFF". Surfaces the UEX 90-day
    /// evaluation gotcha right next to the JSON preview.</summary>
    public string AttachScreenshotLabel { get; }
    public bool AttachScreenshot { get; }

    /// <summary>"Delete sources after submit: ON" / "OFF". Reminds the user
    /// what happens to the original .png files on disk after a successful
    /// production submission.</summary>
    public string DeleteAfterSubmitLabel { get; }
    public bool DeleteAfterSubmit { get; }

    /// <summary>"Throttle: 1000 ms between submissions" — concrete reminder
    /// of the rate limit setting so the user can guess the total batch
    /// duration before clicking Send all.</summary>
    public string ThrottleLabel { get; }

    /// <summary>"Estimated duration: ~6 s" — a friendly extrapolation of
    /// (submissions − 1) × throttle so the user knows roughly how long the
    /// batch will tie up the UI.</summary>
    public string EstimatedDurationLabel { get; }

    partial void OnSubmissionsCountChanged(int value) => OnPropertyChanged(nameof(Summary));
    partial void OnCommoditiesSentChanged(int value) => OnPropertyChanged(nameof(Summary));
    partial void OnCommoditiesDedupedChanged(int value) => OnPropertyChanged(nameof(Summary));

    public BatchPreviewViewModel(
        BatchPlan plan,
        BatchOptions options,
        IBatchPayloadFactory payloadFactory,
        IAppPreferences prefs)
    {
        SubmissionsCount = plan.Submissions.Count;
        CommoditiesSent = plan.TotalCommoditiesSent;
        CommoditiesDeduped = plan.TotalCommoditiesDeduped;

        // Materialise every "kept" row first so the user sees what will
        // actually be sent before scrolling into the deduplicated tail.
        // The Side column makes the BUY/SELL split explicit so the user
        // never wonders why two screens of the same terminal both passed
        // through with the same commodity (different tabs = different data).
        foreach (var s in plan.Submissions)
        {
            foreach (var r in s.Rows)
            {
                if (r.IdCommodity is not int cid) continue;
                Rows.Add(new BatchPreviewRow(
                    CommodityLabel: r.CommodityName ?? $"#{cid}",
                    TerminalLabel: s.TerminalLabel,
                    SideLabel: TabLabel(s.Tab),
                    FoundOnLabel: $"#{s.QueuePosition}",
                    SentOnLabel: $"#{s.QueuePosition}",
                    Reason: "Kept",
                    IsKept: true));
            }
        }

        // Then the deduped ones — same DataGrid so the user reads a single
        // table per commodity. The dedup never crosses BUY/SELL so this
        // section only contains same-tab collisions.
        foreach (var d in plan.DedupedRows)
        {
            Rows.Add(new BatchPreviewRow(
                CommodityLabel: d.CommodityLabel,
                TerminalLabel: d.TerminalLabel,
                SideLabel: TabLabel(d.Tab),
                FoundOnLabel: $"#{d.FoundOnQueuePosition}",
                SentOnLabel: d.AssignedToQueuePosition > 0
                    ? $"#{d.AssignedToQueuePosition}"
                    : "(skipped)",
                Reason: d.Reason,
                IsKept: false));
        }

        // Build the per-submission previews. We materialise the wire JSON
        // up-front so the user can flip between submissions in the dialog
        // without UI lag, even when the screenshot attachment forces a base64
        // re-encode (~30 ms for a 2 MB png).
        foreach (var s in plan.Submissions)
        {
            Submissions.Add(BuildPreview(s, options, payloadFactory));
        }
        SelectedSubmission = Submissions.FirstOrDefault();

        // ---- Snapshot the prefs into stable display labels ----

        // Mode pill: detect "mixed" when at least one item carries an explicit
        // override that disagrees with the global default. This is the single
        // most surprising thing for users (one item could be PRODUCTION while
        // the rest are TEST), so we surface it loudly.
        var anyProdOverride = plan.Submissions.Any(s => s.SourceItem.DraftIsProduction == true);
        var anyTestOverride = plan.Submissions.Any(s => s.SourceItem.DraftIsProduction == false);
        var globalProd = options.DefaultIsProduction;
        var modeMatches = plan.Submissions.All(s => (s.SourceItem.DraftIsProduction ?? globalProd) == globalProd);
        IsMixedMode = !modeMatches && (anyProdOverride && anyTestOverride
                                       || (anyProdOverride && !globalProd)
                                       || (anyTestOverride && globalProd));
        IsProductionMode = !IsMixedMode && globalProd;
        ModeLabel = IsMixedMode
            ? "Mode: MIXED (per-item override — see JSON tab)"
            : (globalProd ? "Mode: PRODUCTION (live to UEX)" : "Mode: TEST (recorded only)");

        // Branch pill: LIVE if all items are LIVE, PTU if all PTU, MIXED if
        // both branches are represented.
        var hasLive = plan.Submissions.Any(s => s.SourceItem.Branch == GameBranch.Live);
        var hasPtu = plan.Submissions.Any(s => s.SourceItem.Branch == GameBranch.Ptu);
        IsMixedBranch = hasLive && hasPtu;
        HasPtuItems = hasPtu;
        BranchLabel = IsMixedBranch
            ? "Branch: MIXED (LIVE + PTU)"
            : (hasPtu ? "Branch: PTU" : "Branch: LIVE");

        AttachScreenshot = prefs.AttachScreenshotOnSubmit;
        AttachScreenshotLabel = AttachScreenshot
            ? "Attach screenshots: ON (UEX gets visual evidence with each POST)"
            : "Attach screenshots: OFF (UEX may reject within the 90-day evaluation period)";

        DeleteAfterSubmit = prefs.DeleteScreenshotAfterSubmit;
        DeleteAfterSubmitLabel = DeleteAfterSubmit
            ? "Delete sources after submit: ON (each .png is removed once UEX accepts it)"
            : "Delete sources after submit: OFF (.png files stay in the watched folder)";

        var throttleMs = Math.Max(0, prefs.BatchSubmissionDelayMs);
        ThrottleLabel = throttleMs > 0
            ? $"Throttle: {throttleMs} ms between submissions"
            : "Throttle: none (back-to-back POSTs)";

        var estMs = Math.Max(0, plan.Submissions.Count - 1) * (long)throttleMs;
        EstimatedDurationLabel = estMs >= 60_000
            ? $"~{estMs / 60_000} min {estMs % 60_000 / 1000} s of throttle"
            : $"~{Math.Max(1, estMs / 1000)} s of throttle";
    }

    /// <summary>
    /// Builds the per-screenshot preview entry, including the exact wire JSON
    /// (with the base64 screenshot collapsed to a marker so the textbox stays
    /// readable). Failures during payload build (eg. missing tab) are caught
    /// and surfaced as the JSON content so the user understands why a row
    /// can't be sent without crashing the dialog.
    /// </summary>
    private static PlannedSubmissionPreview BuildPreview(
        PlannedSubmission planned,
        BatchOptions options,
        IBatchPayloadFactory factory)
    {
        var item = planned.SourceItem;
        var imageName = Path.GetFileName(item.ImagePath);
        long imageBytes = 0;
        try { imageBytes = new FileInfo(item.ImagePath).Length; } catch { /* missing file → 0 */ }

        string previewJson;
        string screenshotInfo;
        var ok = false;
        try
        {
            var payload = factory.BuildPayload(planned, options);

            // Replace the multi-MB base64 string with a placeholder so the
            // user can read the JSON. The size hint stays so they know the
            // attachment is REALLY there.
            string? originalScreenshot = payload.Screenshot;
            if (!string.IsNullOrEmpty(originalScreenshot))
            {
                payload.Screenshot = $"[base64 screenshot — {originalScreenshot.Length:N0} chars, {imageName}]";
            }

            previewJson = UexApiClient.SerialiseWirePayload(payload);
            payload.Screenshot = originalScreenshot;

            screenshotInfo = string.IsNullOrEmpty(originalScreenshot)
                ? $"No screenshot attached for {imageName} (Settings → Attach screenshots is OFF, or file missing)."
                : $"Attaching {imageName} ({FormatBytes(imageBytes)} on disk → ~{FormatBytes(originalScreenshot.Length * 3L / 4L)} base64) in the `screenshot` field.";
            ok = true;
        }
        catch (Exception ex)
        {
            previewJson = $"// Could not build payload for screenshot #{planned.QueuePosition}\n// {ex.Message}";
            screenshotInfo = $"Payload build failed — this submission will go to Failed without ever hitting UEX.";
        }

        var commodityCount = planned.Rows.Count(r => r.IdCommodity.HasValue);
        var sideLabel = TabLabel(planned.Tab);
        return new PlannedSubmissionPreview(
            QueuePosition: planned.QueuePosition,
            TerminalLabel: planned.TerminalLabel,
            SideLabel: sideLabel,
            CommoditiesCount: commodityCount,
            ImageName: imageName ?? "(no source file)",
            DisplayLabel: $"#{planned.QueuePosition} — {planned.TerminalLabel} [{sideLabel}] ({commodityCount} commodity)",
            PreviewJson: previewJson,
            ScreenshotInfo: screenshotInfo,
            CanSend: ok);
    }

    /// <summary>Compact BUY / SELL / ? label for the preview UI columns.</summary>
    private static string TabLabel(TerminalTab tab) => tab switch
    {
        TerminalTab.Buy => "BUY",
        TerminalTab.Sell => "SELL",
        _ => "?",
    };

    /// <summary>Compact byte-size formatter (kB / MB) for the screenshot blurb.</summary>
    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        const long kb = 1024L;
        const long mb = 1024L * 1024L;
        if (bytes >= mb) return $"{(double)bytes / mb:F1} MB";
        if (bytes >= kb) return $"{(double)bytes / kb:F0} kB";
        return $"{bytes} B";
    }
}

/// <summary>
/// One row in the BatchPreviewDialog smart-split grid. Kept rows surface in
/// green-tinted styling, deduped rows in red — the binding lives in the
/// dialog XAML so the row record stays a plain data carrier. <see cref="SideLabel"/>
/// is the BUY / SELL identifier that makes the per-tab dedup explicit (a row
/// kept on BUY does NOT collide with the same commodity on SELL).
/// </summary>
public sealed record BatchPreviewRow(
    string CommodityLabel,
    string TerminalLabel,
    string SideLabel,
    string FoundOnLabel,
    string SentOnLabel,
    string Reason,
    bool IsKept);

/// <summary>
/// One outgoing POST as it will appear on the wire. Carries the pre-rendered
/// JSON body (with the base64 screenshot collapsed to a marker) plus a
/// human-readable summary of the screenshot attachment so the user can read
/// it at a glance instead of scrolling through MB of base64.
/// <see cref="SideLabel"/> exposes the terminal tab (BUY / SELL) so the user
/// can tell at a glance which side this submission targets — important when
/// two screens of the same terminal both show up in the JSON list.
/// </summary>
public sealed record PlannedSubmissionPreview(
    int QueuePosition,
    string TerminalLabel,
    string SideLabel,
    int CommoditiesCount,
    string ImageName,
    string DisplayLabel,
    string PreviewJson,
    string ScreenshotInfo,
    bool CanSend);
