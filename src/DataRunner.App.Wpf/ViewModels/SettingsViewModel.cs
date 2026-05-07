using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DataRunner.App.Services;
using DataRunner.Core.Abstractions;
using DataRunner.Core.Models;
using Microsoft.Extensions.Logging;

namespace DataRunner.App.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ISecretKeyStore _secretStore;
    private readonly ICatalogProvider _catalog;
    private readonly IAppPreferences _prefs;
    private readonly IBuiltInAppTokenProvider _builtInToken;
    private readonly IDialogService _dialog;
    private readonly IGameVersionsService _gameVersions;
    private readonly ILogger<SettingsViewModel> _logger;

    /// <summary>True when the build embeds a UEX app bearer token (official
    /// CI release). The Settings view collapses the bearer override section
    /// behind an "Advanced" expander when this is true.</summary>
    public bool HasBuiltInBearerToken => _builtInToken.HasToken;

    /// <summary>One-line summary describing which bearer token the next
    /// /data_submit will use, used as the header of the bearer override
    /// section so the user always knows where they stand.</summary>
    public string BearerTokenStatus => (HasBuiltInBearerToken, HasBearerToken) switch
    {
        (true, true)   => "Using your custom override (built-in token ignored).",
        (true, false)  => "Using the built-in app token from this build (recommended).",
        (false, true)  => "Using your custom override (no built-in token in this build).",
        (false, false) => "No app token configured. Submissions will fail until you set one or use an official build.",
    };

    /// <summary>
    /// Re-exposed so the Settings view can bind its "Updates" card to the
    /// shared singleton instance (same one driving the status-bar pill).
    /// </summary>
    public UpdateViewModel Updates { get; }

    /// <summary>
    /// Suppresses the partial-property change handlers during the constructor,
    /// so hydrating ScreenshotsFolder / AttachScreenshotOnSubmit from prefs
    /// does NOT trigger a redundant SaveAsync. Without this flag, the constructor
    /// can race the user's first edit and overwrite their value with the default.
    /// </summary>
    private bool _isHydrating = true;

    [ObservableProperty] private string _secretKeyInput = "";
    [ObservableProperty] private bool _hasSecretKey;
    [ObservableProperty] private string _bearerTokenInput = "";
    [ObservableProperty] private bool _hasBearerToken;

    /// <summary>
    /// Star Citizen LIVE-channel screenshots folder. Files dropped here are
    /// auto-imported and tagged <see cref="GameBranch.Live"/>; the resulting
    /// /data_submit payload carries the current LIVE build number from
    /// /game_versions in its <c>game_version</c> field.
    /// </summary>
    [ObservableProperty] private string _liveScreenshotsFolder = "";

    /// <summary>
    /// Optional PTU-channel screenshots folder. Files dropped here are
    /// tagged <see cref="GameBranch.Ptu"/> and submitted with the current
    /// PTU build number. Leave empty if you don't run PTU.
    /// </summary>
    [ObservableProperty] private string _ptuScreenshotsFolder = "";

    /// <summary>
    /// Back-compat alias: existing code (FirstRunWizard, InboxViewModel,
    /// older watcher consumers) still references <c>ScreenshotsFolder</c>.
    /// We forward it to the LIVE slot so nothing breaks while the views
    /// migrate to the new properties.
    /// </summary>
    public string ScreenshotsFolder
    {
        get => LiveScreenshotsFolder;
        set => LiveScreenshotsFolder = value;
    }

    [ObservableProperty] private bool _useFluentMica = true;
    [ObservableProperty] private string _appLanguage = "en";
    [ObservableProperty] private string _catalogStatus = "loading...";
    [ObservableProperty] private bool _attachScreenshotOnSubmit = true;
    [ObservableProperty] private bool _deleteScreenshotAfterSubmit = true;
    [ObservableProperty] private bool _defaultIsProduction;

    /// <summary>
    /// Delay in milliseconds between two consecutive POSTs of the same batch
    /// send. Surfaces <see cref="IAppPreferences.BatchSubmissionDelayMs"/> in
    /// the Settings view so the user can tune the throttle (1000 ms by
    /// default — generous enough to never burst the UEX 1000-reports/30-min
    /// rate cap on realistic batch sizes).
    /// </summary>
    [ObservableProperty] private int _batchSubmissionDelayMs = 1000;

    [ObservableProperty] private string _liveScreenshotsFolderStatus = "";
    [ObservableProperty] private bool _liveScreenshotsFolderHasError;
    [ObservableProperty] private string _ptuScreenshotsFolderStatus = "";
    [ObservableProperty] private bool _ptuScreenshotsFolderHasError;

    /// <summary>
    /// Read-only label shown next to the LIVE folder picker. Resolved at
    /// runtime from the cached <see cref="IGameVersionsService"/> so the
    /// user sees what <c>game_version</c> their submissions will carry.
    /// </summary>
    [ObservableProperty] private string _liveGameVersionLabel = "";

    /// <summary>Same idea as <see cref="LiveGameVersionLabel"/>, for the PTU slot.</summary>
    [ObservableProperty] private string _ptuGameVersionLabel = "";

    /// <summary>Severity name compatible with <c>SeverityToBrushConverter</c>.</summary>
    public string LiveScreenshotsFolderSeverity => LiveScreenshotsFolderHasError ? "Warning" : "Ok";
    public string PtuScreenshotsFolderSeverity => PtuScreenshotsFolderHasError ? "Warning" : "Ok";

    partial void OnLiveScreenshotsFolderHasErrorChanged(bool value)
        => OnPropertyChanged(nameof(LiveScreenshotsFolderSeverity));
    partial void OnPtuScreenshotsFolderHasErrorChanged(bool value)
        => OnPropertyChanged(nameof(PtuScreenshotsFolderSeverity));

    /// <summary>
    /// Placeholder shown inside the secret-key PasswordBox. Differs depending on
    /// whether the user already saved a key (we never re-display it in clear text).
    /// </summary>
    public string KeyInputPlaceholder => HasSecretKey
        ? "A key is already saved — paste a new one here only to replace it"
        : "Paste your secret-key here";

    public string BearerInputPlaceholder => HasBearerToken
        ? "An app token is already saved — paste a new one here only to replace it"
        : "Paste your app bearer token here";

    partial void OnHasSecretKeyChanged(bool value) => OnPropertyChanged(nameof(KeyInputPlaceholder));
    partial void OnHasBearerTokenChanged(bool value)
    {
        OnPropertyChanged(nameof(BearerInputPlaceholder));
        OnPropertyChanged(nameof(BearerTokenStatus));
    }

    partial void OnAttachScreenshotOnSubmitChanged(bool value)
    {
        if (_isHydrating) return;
        _prefs.AttachScreenshotOnSubmit = value;
        _ = SavePrefsAsync("attach-screenshot");
    }

    partial void OnDeleteScreenshotAfterSubmitChanged(bool value)
    {
        if (_isHydrating) return;
        _prefs.DeleteScreenshotAfterSubmit = value;
        _ = SavePrefsAsync("delete-screenshot-after-submit");
    }

    partial void OnDefaultIsProductionChanged(bool value)
    {
        if (_isHydrating) return;
        _prefs.DefaultIsProduction = value;
        _ = SavePrefsAsync("default-is-production");
    }

    partial void OnBatchSubmissionDelayMsChanged(int value)
    {
        if (_isHydrating) return;
        // Defensive clamp: keep the value sane even if the user typed a
        // negative number directly in the input. We never write back the
        // clamped value to the property here (would cause a re-entry); we
        // only forward the clamped value to the prefs file.
        var clamped = Math.Max(0, value);
        _prefs.BatchSubmissionDelayMs = clamped;
        _ = SavePrefsAsync("batch-submission-delay-ms");
    }

    partial void OnLiveScreenshotsFolderChanged(string value)
    {
        // Forward the back-compat alias too so listeners bound to
        // ScreenshotsFolder (e.g. older watcher code paths) still react.
        OnPropertyChanged(nameof(ScreenshotsFolder));

        if (_isHydrating)
        {
            UpdateLiveStatus(value);
            return;
        }
        _prefs.LiveScreenshotsFolder = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        UpdateLiveStatus(value);
        _ = SavePrefsAsync("live-screenshots-folder");
    }

    partial void OnPtuScreenshotsFolderChanged(string value)
    {
        if (_isHydrating)
        {
            UpdatePtuStatus(value);
            return;
        }
        _prefs.PtuScreenshotsFolder = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        UpdatePtuStatus(value);
        _ = SavePrefsAsync("ptu-screenshots-folder");
    }

    /// <summary>Awaited save with logging + UI feedback. Replaces the previous fire-and-forget pattern that swallowed errors.</summary>
    private async Task SavePrefsAsync(string field)
    {
        try
        {
            await _prefs.SaveAsync().ConfigureAwait(true);
            _logger.LogInformation(
                "Preferences saved (trigger={Field}, live={Live}, ptu={Ptu}).",
                field,
                _prefs.LiveScreenshotsFolder ?? "<null>",
                _prefs.PtuScreenshotsFolder ?? "<null>");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save preferences (trigger={Field}).", field);
            LiveScreenshotsFolderStatus = $"Save failed: {ex.Message}";
            LiveScreenshotsFolderHasError = true;
        }
    }

    private void UpdateLiveStatus(string value)
        => UpdateFolderStatus(value, isLive: true,
            absent: "No LIVE folder configured — auto-import disabled for LIVE screenshots.",
            ok: "Watching this folder for new LIVE screenshots.");

    private void UpdatePtuStatus(string value)
        => UpdateFolderStatus(value, isLive: false,
            absent: "No PTU folder configured (optional — leave empty if you don't run PTU).",
            ok: "Watching this folder for new PTU screenshots.");

    private void UpdateFolderStatus(string value, bool isLive, string absent, string ok)
    {
        string status; bool hasError;
        if (string.IsNullOrWhiteSpace(value))
        {
            status = absent;
            // Empty PTU folder is NOT an error (it's optional); empty LIVE is.
            hasError = isLive;
        }
        else
        {
            var trimmed = value.Trim();
            if (!Directory.Exists(trimmed))
            {
                status = $"Folder does not exist: {trimmed}";
                hasError = true;
            }
            else
            {
                status = ok;
                hasError = false;
            }
        }

        if (isLive)
        {
            LiveScreenshotsFolderStatus = status;
            LiveScreenshotsFolderHasError = hasError;
        }
        else
        {
            PtuScreenshotsFolderStatus = status;
            PtuScreenshotsFolderHasError = hasError;
        }
    }

    public SettingsViewModel(
        ISecretKeyStore secretStore,
        ICatalogProvider catalog,
        IAppPreferences prefs,
        IBuiltInAppTokenProvider builtInToken,
        IDialogService dialog,
        IGameVersionsService gameVersions,
        UpdateViewModel updates,
        ILogger<SettingsViewModel> logger)
    {
        _secretStore = secretStore;
        _catalog = catalog;
        _prefs = prefs;
        _builtInToken = builtInToken;
        _dialog = dialog;
        _gameVersions = gameVersions;
        _logger = logger;
        Updates = updates;

        _ = RefreshAsync();
        _catalog.Refreshed += (_, _) => CatalogStatus = BuildCatalogStatus();

        // Hydrate the UI from the persisted prefs. Fall back to the SC default
        // screenshots folder only if the user hasn't picked one yet.
        // _isHydrating prevents the partial setters from immediately re-saving
        // (which used to fire-and-forget overwrite the user value with "").
        try
        {
            AttachScreenshotOnSubmit = _prefs.AttachScreenshotOnSubmit;
            DeleteScreenshotAfterSubmit = _prefs.DeleteScreenshotAfterSubmit;
            DefaultIsProduction = _prefs.DefaultIsProduction;
            BatchSubmissionDelayMs = _prefs.BatchSubmissionDelayMs;
            LiveScreenshotsFolder = _prefs.LiveScreenshotsFolder ?? DefaultScreenshotsFolder(GameBranch.Live);
            PtuScreenshotsFolder = _prefs.PtuScreenshotsFolder ?? DefaultScreenshotsFolder(GameBranch.Ptu);
            UpdateLiveStatus(LiveScreenshotsFolder);
            UpdatePtuStatus(PtuScreenshotsFolder);
            UpdateGameVersionLabels();
            _logger.LogInformation(
                "SettingsViewModel hydrated: live={Live}, ptu={Ptu}",
                _prefs.LiveScreenshotsFolder ?? "<null>",
                _prefs.PtuScreenshotsFolder ?? "<null>");
        }
        finally
        {
            _isHydrating = false;
        }
    }

    /// <summary>
    /// Refreshes the per-folder labels showing what <c>game_version</c> string
    /// each slot will send. Re-called whenever the user picks a new folder
    /// or after a successful /game_versions fetch.
    /// </summary>
    public void UpdateGameVersionLabels()
    {
        var c = _gameVersions.Cached;
        var live = string.IsNullOrWhiteSpace(c?.Live) ? "LIVE (literal — UEX will use its current LIVE build)" : c!.Live!;
        var ptu = string.IsNullOrWhiteSpace(c?.Ptu)
            ? "PTU (no current build — UEX may reject the report until PTU re-opens)"
            : c!.Ptu!;
        LiveGameVersionLabel = $"Submissions tag: game_version = \"{live}\"";
        PtuGameVersionLabel = $"Submissions tag: game_version = \"{ptu}\"";
    }

    [RelayCommand]
    private void BrowseLiveScreenshotsFolder()
        => BrowseFolder(GameBranch.Live);

    [RelayCommand]
    private void BrowsePtuScreenshotsFolder()
        => BrowseFolder(GameBranch.Ptu);

    private void BrowseFolder(GameBranch branch)
    {
        var current = branch == GameBranch.Live ? LiveScreenshotsFolder : PtuScreenshotsFolder;
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = branch == GameBranch.Live
                ? "Pick the LIVE Star Citizen screenshots folder (...\\StarCitizen\\LIVE\\Screenshots)"
                : "Pick the PTU Star Citizen screenshots folder (...\\StarCitizen\\PTU\\Screenshots)",
            InitialDirectory = !string.IsNullOrWhiteSpace(current) && Directory.Exists(current)
                ? current
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        if (dlg.ShowDialog() == true)
        {
            if (branch == GameBranch.Live) LiveScreenshotsFolder = dlg.FolderName;
            else PtuScreenshotsFolder = dlg.FolderName;
        }
    }

    /// <summary>
    /// Probes the standard Star Citizen install layouts for a Screenshots
    /// folder matching the given branch. Returns the first one that exists,
    /// or empty when none is found (the user will have to Browse manually).
    /// </summary>
    private static string DefaultScreenshotsFolder(GameBranch branch)
    {
        var subfolder = branch == GameBranch.Ptu ? "PTU" : "LIVE";
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // The Roberts Space Industries pictures folder is shared across
        // branches so we only return it for LIVE — it's the historical default
        // and the most likely starting point on a fresh install.
        if (branch == GameBranch.Live)
        {
            var roberts = Path.Combine(userProfile, "Pictures", "Roberts Space Industries", "ScreenShots");
            if (Directory.Exists(roberts)) return roberts;
        }

        // Per-branch install probes: D:\StarCitizen\{LIVE|PTU}\Screenshots,
        // D:\Program Files\Roberts Space Industries\StarCitizen\..., etc.
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != System.IO.DriveType.Fixed || !drive.IsReady) continue;
            var root = drive.RootDirectory.FullName;
            string[] candidates =
            [
                Path.Combine(root, "StarCitizen", subfolder, "Screenshots"),
                Path.Combine(root, "Program Files", "Roberts Space Industries", "StarCitizen", subfolder, "Screenshots"),
                Path.Combine(root, "Jeux", "StarCitizen", subfolder, "Screenshots"),
                Path.Combine(root, "Games", "StarCitizen", subfolder, "Screenshots"),
            ];
            foreach (var c in candidates)
            {
                if (Directory.Exists(c)) return c;
            }
        }

        return "";
    }

    private string BuildCatalogStatus()
        => _catalog.LastRefreshedAt is { } at
            ? $"{_catalog.Commodities.Count} commodities, {_catalog.CommodityTerminals.Count} terminals · refreshed {at.LocalDateTime:g}"
            : "not yet loaded";

    public async Task RefreshAsync()
    {
        HasSecretKey = await _secretStore.HasKeyAsync();
        HasBearerToken = await _secretStore.HasBearerTokenAsync();
        CatalogStatus = BuildCatalogStatus();

        // Re-read prefs from disk so changes made OUTSIDE SettingsViewModel
        // (eg. by the first-run wizard, which writes directly to IAppPreferences)
        // are picked up. Without this the ScreenshotsFolder property stays at
        // its constructor-time value even though prefs.json now has the folder
        // the user picked in the wizard → the FileSystemWatcher (which listens
        // on ScreenshotsFolder's PropertyChanged) never reconfigures and new
        // screenshots are silently ignored until the user reopens Settings.
        _isHydrating = true;
        try
        {
            await _prefs.LoadAsync();
            AttachScreenshotOnSubmit = _prefs.AttachScreenshotOnSubmit;
            DeleteScreenshotAfterSubmit = _prefs.DeleteScreenshotAfterSubmit;
            DefaultIsProduction = _prefs.DefaultIsProduction;
            BatchSubmissionDelayMs = _prefs.BatchSubmissionDelayMs;

            var freshLive = _prefs.LiveScreenshotsFolder ?? DefaultScreenshotsFolder(GameBranch.Live);
            if (!string.Equals(LiveScreenshotsFolder, freshLive, StringComparison.OrdinalIgnoreCase))
            {
                _isHydrating = false;
                LiveScreenshotsFolder = freshLive;
                _isHydrating = true;
            }

            var freshPtu = _prefs.PtuScreenshotsFolder ?? DefaultScreenshotsFolder(GameBranch.Ptu);
            if (!string.Equals(PtuScreenshotsFolder, freshPtu, StringComparison.OrdinalIgnoreCase))
            {
                _isHydrating = false;
                PtuScreenshotsFolder = freshPtu;
                _isHydrating = true;
            }

            UpdateLiveStatus(LiveScreenshotsFolder);
            UpdatePtuStatus(PtuScreenshotsFolder);
            UpdateGameVersionLabels();
        }
        finally
        {
            _isHydrating = false;
        }
    }

    [RelayCommand]
    private async Task SaveSecretKeyAsync()
    {
        var trimmed = SecretKeyInput.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            _dialog.ShowError("Empty key", "Please paste a non-empty secret-key.");
            return;
        }
        await _secretStore.SetAsync(trimmed);
        SecretKeyInput = "";
        HasSecretKey = true;
        _dialog.ShowInfo("Secret key saved",
            "Your UEX secret-key is now encrypted on disk via Windows DPAPI (current user only).");
    }

    [RelayCommand]
    private async Task ClearSecretKeyAsync()
    {
        await _secretStore.ClearAsync();
        HasSecretKey = false;
    }

    [RelayCommand]
    private async Task SaveBearerTokenAsync()
    {
        var trimmed = BearerTokenInput.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            _dialog.ShowError("Empty token", "Please paste a non-empty app bearer token.");
            return;
        }
        await _secretStore.SetBearerTokenAsync(trimmed);
        BearerTokenInput = "";
        HasBearerToken = true;
        _dialog.ShowInfo("App token saved",
            "Your UEX app bearer token is now encrypted on disk via Windows DPAPI (current user only).");
    }

    [RelayCommand]
    private async Task ClearBearerTokenAsync()
    {
        await _secretStore.ClearBearerTokenAsync();
        HasBearerToken = false;
    }

    [RelayCommand]
    private void OpenUexAppsPage()
        => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "https://uexcorp.space/api/apps",
            UseShellExecute = true,
        });

    [RelayCommand]
    private async Task RefreshCatalogAsync()
    {
        try
        {
            await _catalog.RefreshAsync(force: true);
        }
        catch (Exception ex)
        {
            _dialog.ShowError("Catalog refresh failed", ex.Message);
        }
    }

    [RelayCommand]
    private async Task RefreshGameVersionsAsync()
    {
        try
        {
            var versions = await _gameVersions.RefreshAsync();
            UpdateGameVersionLabels();
            _dialog.ShowInfo("Game versions refreshed",
                $"UEX is currently using:\n  LIVE = {versions.Live ?? "<not set>"}\n  PTU  = {versions.Ptu ?? "<no current PTU build>"}");
        }
        catch (Exception ex)
        {
            _dialog.ShowError("Game versions refresh failed", ex.Message);
        }
    }

    [RelayCommand]
    private void OpenUexAccountPage()
        => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            // Account main page — the user secret-key lives in the "Secret Key"
            // section there. NOT to be confused with /api/apps which generates
            // *application* bearer tokens and won't be accepted on /data_submit.
            FileName = "https://uexcorp.space/account",
            UseShellExecute = true,
        });
}
