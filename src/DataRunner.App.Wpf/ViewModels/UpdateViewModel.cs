using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DataRunner.App.Services;
using Microsoft.Extensions.Logging;
using Velopack;

namespace DataRunner.App.ViewModels;

/// <summary>
/// Drives the "Updates" card in Settings AND the status-bar pill in the main
/// window. Both views bind to the SAME singleton instance so a check kicked
/// off from either entry point updates both.
/// </summary>
public sealed partial class UpdateViewModel : ObservableObject
{
    private readonly IUpdateService _updates;
    private readonly ILogger<UpdateViewModel> _logger;

    /// <summary>
    /// The Velopack <see cref="UpdateInfo"/> we're currently working with —
    /// kept in a private field so commands can reuse it across the
    /// "check → download → apply" flow without re-querying GitHub.
    /// </summary>
    private UpdateInfo? _pending;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowInStatusBar))]
    [NotifyPropertyChangedFor(nameof(StatusLabel))]
    private UpdateState _state = UpdateState.Idle;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusLabel))]
    private string _currentVersion = "0.0.0";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusLabel))]
    private string? _latestVersion;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusLabel))]
    private int _downloadProgress;

    [ObservableProperty] private string? _lastError;
    [ObservableProperty] private DateTimeOffset? _lastCheckedAt;
    [ObservableProperty] private bool _isInstalled;

    /// <summary>True while the silent or manual check is mid-flight.</summary>
    public bool IsBusy => State is UpdateState.Checking or UpdateState.Downloading;

    /// <summary>
    /// True when the status-bar pill should be visible. We hide it during
    /// idle/dev builds so users never see "Up to date" noise.
    /// </summary>
    public bool ShowInStatusBar => State is UpdateState.Available
                                   or UpdateState.Downloading
                                   or UpdateState.ReadyToApply
                                   or UpdateState.Failed;

    public string StatusLabel => State switch
    {
        UpdateState.Idle           => $"v{CurrentVersion}",
        UpdateState.Checking       => "Checking for updates…",
        UpdateState.UpToDate       => $"Up to date · v{CurrentVersion}",
        UpdateState.Available      => $"Update available: v{LatestVersion}",
        UpdateState.Downloading    => $"Downloading v{LatestVersion}… {DownloadProgress}%",
        UpdateState.ReadyToApply   => $"Restart to apply v{LatestVersion}",
        UpdateState.Failed         => "Update check failed",
        _                          => "",
    };

    partial void OnStateChanged(UpdateState value)
    {
        OnPropertyChanged(nameof(IsBusy));
        CheckCommand.NotifyCanExecuteChanged();
        DownloadCommand.NotifyCanExecuteChanged();
        ApplyAndRestartCommand.NotifyCanExecuteChanged();
    }

    public UpdateViewModel(IUpdateService updates, ILogger<UpdateViewModel> logger)
    {
        _updates = updates;
        _logger = logger;
        CurrentVersion = updates.CurrentVersion;
        IsInstalled = updates.IsInstalled;
    }

    /// <summary>
    /// Fired from <c>App.OnStartup</c>. Same code path as the manual button,
    /// but failures are logged silently and the UI never flips to a scary
    /// "Failed" state on a transient cold-start network glitch.
    /// </summary>
    public async Task CheckForUpdatesSilentlyAsync()
    {
        if (!IsInstalled) return;
        try
        {
            await CheckCoreAsync(silent: true).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Silent update check failed (will retry next launch).");
        }
    }

    [RelayCommand(CanExecute = nameof(CanCheck))]
    private async Task CheckAsync()
    {
        await CheckCoreAsync(silent: false).ConfigureAwait(true);
    }
    private bool CanCheck() => !IsBusy;

    private async Task CheckCoreAsync(bool silent)
    {
        State = UpdateState.Checking;
        LastError = null;

        var info = await _updates.CheckForUpdatesAsync().ConfigureAwait(true);
        LastCheckedAt = DateTimeOffset.Now;
        _pending = info;

        if (info is null)
        {
            LatestVersion = null;
            State = UpdateState.UpToDate;
            return;
        }

        LatestVersion = info.TargetFullRelease.Version.ToString();
        State = UpdateState.Available;

        // For silent startup probes, we stop here so the user can decide
        // whether to download. Manual checks bound to the button do the same;
        // download is a separate, explicit click to respect data caps.
        if (silent) _logger.LogInformation("Update {V} available (silent).", LatestVersion);
    }

    [RelayCommand(CanExecute = nameof(CanDownload))]
    private async Task DownloadAsync()
    {
        if (_pending is null) return;
        try
        {
            State = UpdateState.Downloading;
            DownloadProgress = 0;
            await _updates.DownloadUpdatesAsync(_pending, new Progress<int>(p => DownloadProgress = p))
                .ConfigureAwait(true);
            State = UpdateState.ReadyToApply;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download update {Version}.", LatestVersion);
            LastError = ex.Message;
            State = UpdateState.Failed;
        }
    }
    private bool CanDownload() => State == UpdateState.Available && _pending is not null;

    [RelayCommand(CanExecute = nameof(CanApply))]
    private void ApplyAndRestart()
    {
        if (_pending is null) return;
        // ApplyUpdatesAndRestart kills the current process; nothing past this
        // line will ever run on the happy path.
        _updates.ApplyUpdatesAndRestart(_pending);
    }
    private bool CanApply() => State == UpdateState.ReadyToApply && _pending is not null;
}

public enum UpdateState
{
    Idle,
    Checking,
    UpToDate,
    Available,
    Downloading,
    ReadyToApply,
    Failed,
}
