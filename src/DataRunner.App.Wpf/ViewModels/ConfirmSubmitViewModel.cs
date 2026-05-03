using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DataRunner.Core.Abstractions;
using DataRunner.Core.Models;

namespace DataRunner.App.ViewModels;

/// <summary>
/// View model for the "are you sure you want to send?" confirmation dialog.
/// Surfaces the validation result, the duplicate / live-diff report, and the exact
/// JSON body that will be POSTed.
/// </summary>
public sealed partial class ConfirmSubmitViewModel : ObservableObject
{
    public UexDataSubmitPayload Payload { get; }
    public ValidationReport Validation { get; }
    public DuplicateReport Duplicates { get; }
    public string PreviewJson { get; }

    public ObservableCollection<ValidationIssue> Issues { get; } = new();
    public ObservableCollection<DuplicateFinding> Findings { get; } = new();

    /// <summary>
    /// Set by the editor when constructing this dialog. Reflects the user's
    /// global preference (Settings -> "Send submissions in PRODUCTION mode by
    /// default"). Not editable in the dialog anymore — the per-screenshot
    /// toggle was a footgun (one accidental flick committed live data).
    /// </summary>
    [ObservableProperty] private bool _isProduction;
    [ObservableProperty] private bool _acknowledgeWarnings;
    [ObservableProperty] private bool _overrideBlock;

    /// <summary>Read-only label shown in the footer: "TEST" or "PRODUCTION (live)".</summary>
    public string ModeLabel => IsProduction ? "PRODUCTION (live to UEX)" : "TEST (recorded only)";

    public bool HasBlockingIssues =>
        Validation.IsBlocking || Duplicates.Worst == DuplicateSeverity.Block;

    public bool HasWarnings =>
        Validation.HasWarnings || Duplicates.Worst is DuplicateSeverity.Warning or DuplicateSeverity.Info;

    /// <summary>
    /// Send is allowed when:
    ///  - no blocking issue OR the user explicitly overrides the block
    ///  - no warning OR the user has acknowledged them
    /// </summary>
    public bool CanSend =>
        (!HasBlockingIssues || OverrideBlock)
        && (!HasWarnings || AcknowledgeWarnings);

    public string SeverityHeadline => Duplicates.Worst switch
    {
        DuplicateSeverity.Block => "BLOCKED — fix before sending (or override below)",
        DuplicateSeverity.Warning => "REVIEW — anomalies detected",
        DuplicateSeverity.Info => "Ready — informational notes only",
        _ => "Ready",
    };

    public ConfirmSubmitViewModel(
        UexDataSubmitPayload payload,
        ValidationReport validation,
        DuplicateReport duplicates,
        string previewJson)
    {
        Payload = payload;
        Validation = validation;
        Duplicates = duplicates;
        PreviewJson = previewJson;

        foreach (var i in validation.Issues) Issues.Add(i);
        foreach (var f in duplicates.Findings) Findings.Add(f);
    }

    partial void OnAcknowledgeWarningsChanged(bool value) => OnPropertyChanged(nameof(CanSend));
    partial void OnIsProductionChanged(bool value)
    {
        OnPropertyChanged(nameof(CanSend));
        OnPropertyChanged(nameof(ModeLabel));
    }
    partial void OnOverrideBlockChanged(bool value) => OnPropertyChanged(nameof(CanSend));
}
