using Microsoft.Extensions.Logging;
using Velopack;
using Velopack.Sources;

namespace DataRunner.App.Services;

/// <summary>
/// Velopack-backed implementation of <see cref="IUpdateService"/>.
/// <para>
/// Update channel = the public GitHub repo. The first stable release published
/// there will become the floor; everything else is delta-packed by <c>vpk</c>
/// in the GitHub Action so end-user downloads stay in the few-MB range.
/// </para>
/// <para>
/// The class is deliberately tolerant of "no Velopack metadata on disk"
/// (i.e. dev builds). Any operation in that mode short-circuits to no-op
/// instead of throwing, so the UI doesn't have to special-case it.
/// </para>
/// </summary>
public sealed class VelopackUpdateService : IUpdateService
{
    // Public repo URL — must match Directory.Build.props <RepositoryUrl>.
    // Velopack's GithubSource only needs the repo root; it will hit
    // /releases/latest and discover assets from there.
    private const string GithubRepoUrl = "https://github.com/Olrik-WP/SC-DataRunnerNet";

    private readonly UpdateManager _manager;
    private readonly ILogger<VelopackUpdateService> _logger;

    public VelopackUpdateService(ILogger<VelopackUpdateService> logger)
    {
        _logger = logger;

        // prerelease=false: only ship stable tags (vX.Y.Z without "-" suffix)
        // to end users by default. A future "Receive pre-release builds"
        // toggle in Settings can flip this to true.
        var source = new GithubSource(GithubRepoUrl, accessToken: null, prerelease: false);
        _manager = new UpdateManager(source);
    }

    public bool IsInstalled => _manager.IsInstalled;

    public string CurrentVersion => _manager.CurrentVersion?.ToString() ?? App.GetAppVersion();

    public async Task<UpdateInfo?> CheckForUpdatesAsync(CancellationToken ct = default)
    {
        if (!_manager.IsInstalled)
        {
            _logger.LogDebug("Skipping update check: app is not Velopack-installed (dev build).");
            return null;
        }

        try
        {
            var info = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
            if (info is null)
            {
                _logger.LogInformation("Update check: already up to date (current={Version}).", CurrentVersion);
            }
            else
            {
                _logger.LogInformation("Update check: {Latest} is available (current={Current}).",
                    info.TargetFullRelease.Version, CurrentVersion);
            }
            return info;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Update check failed (network down? rate-limited?).");
            return null;
        }
    }

    public Task DownloadUpdatesAsync(UpdateInfo update, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        if (!_manager.IsInstalled) return Task.CompletedTask;

        // Velopack reports raw 0..100; we forward as-is so the VM/UI binds
        // straight onto a ProgressBar without extra math.
        return _manager.DownloadUpdatesAsync(
            update,
            p => progress?.Report(p),
            ct);
    }

    public void ApplyUpdatesAndRestart(UpdateInfo update)
    {
        if (!_manager.IsInstalled) return;
        // ApplyUpdatesAndRestart takes the VelopackAsset (full release pkg),
        // not the UpdateInfo wrapper.
        _manager.ApplyUpdatesAndRestart(update.TargetFullRelease, Array.Empty<string>());
    }
}
