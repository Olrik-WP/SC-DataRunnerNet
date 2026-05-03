using Velopack;

namespace DataRunner.App.Services;

/// <summary>
/// Thin wrapper around Velopack's <see cref="UpdateManager"/> so the rest of
/// the app can consume an idiomatic, mockable async API and never has to
/// reason about whether the current build is "installed" (and thus eligible
/// for updates) or running from a developer Debug folder (and thus NOT).
/// </summary>
public interface IUpdateService
{
    /// <summary>
    /// True when the current process was launched from a Velopack-installed
    /// location (i.e. <c>%LocalAppData%\SC-DataRunnerNet</c>). Always false
    /// when running from <c>bin\Debug</c>; UI should hide the update controls
    /// in that case.
    /// </summary>
    bool IsInstalled { get; }

    /// <summary>SemVer of the currently running build (e.g. "1.2.3").</summary>
    string CurrentVersion { get; }

    /// <summary>
    /// Polls the configured GitHub Releases feed once. Returns null when
    /// already up to date OR when the app is not installed.
    /// </summary>
    Task<UpdateInfo?> CheckForUpdatesAsync(CancellationToken ct = default);

    /// <summary>
    /// Downloads (and verifies the signature of) the package returned by
    /// <see cref="CheckForUpdatesAsync"/>. Reports 0..100 progress.
    /// </summary>
    Task DownloadUpdatesAsync(UpdateInfo update, IProgress<int>? progress = null, CancellationToken ct = default);

    /// <summary>
    /// Restarts the app to apply a previously downloaded update. Does NOT
    /// return: the current process is killed by the Velopack updater.
    /// </summary>
    void ApplyUpdatesAndRestart(UpdateInfo update);
}
