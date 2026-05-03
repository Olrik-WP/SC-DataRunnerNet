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

    /// <summary>Persists the current state to disk.</summary>
    Task SaveAsync(CancellationToken ct = default);

    /// <summary>Reloads from disk (used at startup).</summary>
    Task LoadAsync(CancellationToken ct = default);
}
