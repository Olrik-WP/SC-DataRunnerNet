namespace DataRunner.Core.Abstractions;

/// <summary>
/// User-tunable, NON-secret application preferences. Persisted to a small
/// JSON file under %LOCALAPPDATA%\SC-DataRunnerNet\prefs.json.
/// Secrets (UEX key) live in <see cref="ISecretKeyStore"/>, not here.
/// </summary>
public interface IAppPreferences
{
    /// <summary>
    /// When true, the source screenshot is base64-encoded and attached to every
    /// /data_submit POST as the `screenshot` field.
    ///
    /// REQUIRED for new datarunners (UEX enforces a 90-day evaluation period
    /// during which submissions without a screenshot are rejected with
    /// `not_allowed` / `screenshot_required`). Veteran datarunners may turn
    /// this off after their evaluation period, for privacy / bandwidth reasons.
    ///
    /// Default: true.
    /// </summary>
    bool AttachScreenshotOnSubmit { get; set; }

    /// <summary>Folder watched for incoming screenshots.</summary>
    string? ScreenshotsFolder { get; set; }

    /// <summary>
    /// When true, the source .png file is deleted from disk immediately after a
    /// successful submission to UEX. Skips deletion on test/failed submissions
    /// so users can retry. The submission history (SQLite) keeps a record of
    /// the file name + payload regardless, so nothing is lost from the audit log.
    ///
    /// Default: true (user preference, configurable in Settings).
    /// </summary>
    bool DeleteScreenshotAfterSubmit { get; set; }

    /// <summary>
    /// Default value for the `is_production` flag of every new submission.
    /// When true, submissions go LIVE on UEX (prices update for everyone).
    /// When false, UEX treats them as TEST (recorded but not live).
    ///
    /// Centralized here so the user picks their mode ONCE in Settings instead
    /// of being asked at every screenshot — which is both noisy and dangerous
    /// (a hurried user can flip it on without thinking).
    ///
    /// Default: true. The whole point of running this tool IS contributing
    /// live price data to UEX; users who specifically want to test their
    /// setup can flip it off in Settings.
    /// </summary>
    bool DefaultIsProduction { get; set; }

    /// <summary>
    /// When true, the inbox column is collapsed to a thin strip with just a
    /// re-expand button. Frees ~280px of horizontal space for the editor and
    /// the optional side-by-side screenshot panel. Persisted so the user's
    /// preferred layout survives app restarts.
    ///
    /// Default: false (inbox visible).
    /// </summary>
    bool InboxCollapsed { get; set; }

    /// <summary>
    /// When true, the editor renders the source screenshot in a panel docked
    /// to the right of the validation form, with a draggable splitter between
    /// them. Lets the user verify OCR output against the source image without
    /// alt-tabbing or opening the floating viewer. Auto-disables itself if
    /// the editor area is too narrow to fit both panes (see EditorMinWidth in
    /// ScreenshotEditView).
    ///
    /// Default: false (the user opts in via the title-bar toggle).
    /// </summary>
    bool SideBySideScreenshot { get; set; }

    /// <summary>Persists the current state to disk.</summary>
    Task SaveAsync(CancellationToken ct = default);

    /// <summary>Reloads from disk (used at startup).</summary>
    Task LoadAsync(CancellationToken ct = default);
}
