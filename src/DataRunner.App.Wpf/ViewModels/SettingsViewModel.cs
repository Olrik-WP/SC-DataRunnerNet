using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DataRunner.App.Services;
using DataRunner.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace DataRunner.App.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ISecretKeyStore _secretStore;
    private readonly ICatalogProvider _catalog;
    private readonly IAppPreferences _prefs;
    private readonly IBuiltInAppTokenProvider _builtInToken;
    private readonly IDialogService _dialog;
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
    [ObservableProperty] private string _screenshotsFolder = "";
    [ObservableProperty] private bool _useFluentMica = true;
    [ObservableProperty] private string _appLanguage = "en";
    [ObservableProperty] private string _catalogStatus = "loading...";
    [ObservableProperty] private bool _attachScreenshotOnSubmit = true;
    [ObservableProperty] private bool _deleteScreenshotAfterSubmit = true;
    [ObservableProperty] private bool _defaultIsProduction;
    [ObservableProperty] private string _screenshotsFolderStatus = "";
    [ObservableProperty] private bool _screenshotsFolderHasError;

    /// <summary>Severity name compatible with <c>SeverityToBrushConverter</c>.</summary>
    public string ScreenshotsFolderSeverity => ScreenshotsFolderHasError ? "Warning" : "Ok";

    partial void OnScreenshotsFolderHasErrorChanged(bool value) => OnPropertyChanged(nameof(ScreenshotsFolderSeverity));

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

    partial void OnScreenshotsFolderChanged(string value)
    {
        if (_isHydrating)
        {
            UpdateScreenshotsFolderStatus(value);
            return;
        }
        _prefs.ScreenshotsFolder = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        UpdateScreenshotsFolderStatus(value);
        _ = SavePrefsAsync("screenshots-folder");
    }

    /// <summary>Awaited save with logging + UI feedback. Replaces the previous fire-and-forget pattern that swallowed errors.</summary>
    private async Task SavePrefsAsync(string field)
    {
        try
        {
            await _prefs.SaveAsync().ConfigureAwait(true);
            _logger.LogInformation("Preferences saved (trigger={Field}, screenshotsFolder={Folder}).",
                field, _prefs.ScreenshotsFolder ?? "<null>");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save preferences (trigger={Field}).", field);
            ScreenshotsFolderStatus = $"Save failed: {ex.Message}";
            ScreenshotsFolderHasError = true;
        }
    }

    private void UpdateScreenshotsFolderStatus(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            ScreenshotsFolderStatus = "No folder configured — auto-import disabled.";
            ScreenshotsFolderHasError = true;
            return;
        }
        var trimmed = value.Trim();
        if (!Directory.Exists(trimmed))
        {
            ScreenshotsFolderStatus = $"Folder does not exist: {trimmed}";
            ScreenshotsFolderHasError = true;
            return;
        }
        ScreenshotsFolderStatus = "Watching this folder for new screenshots.";
        ScreenshotsFolderHasError = false;
    }

    public SettingsViewModel(
        ISecretKeyStore secretStore,
        ICatalogProvider catalog,
        IAppPreferences prefs,
        IBuiltInAppTokenProvider builtInToken,
        IDialogService dialog,
        UpdateViewModel updates,
        ILogger<SettingsViewModel> logger)
    {
        _secretStore = secretStore;
        _catalog = catalog;
        _prefs = prefs;
        _builtInToken = builtInToken;
        _dialog = dialog;
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
            ScreenshotsFolder = _prefs.ScreenshotsFolder ?? DefaultScreenshotsFolder();
            UpdateScreenshotsFolderStatus(ScreenshotsFolder);
            _logger.LogInformation(
                "SettingsViewModel hydrated: prefs.ScreenshotsFolder={Pref}, UI={Ui}",
                _prefs.ScreenshotsFolder ?? "<null>", ScreenshotsFolder);
        }
        finally
        {
            _isHydrating = false;
        }
    }

    [RelayCommand]
    private void BrowseScreenshotsFolder()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Pick the folder Star Citizen writes screenshots to (Print Screen)",
            InitialDirectory = !string.IsNullOrWhiteSpace(ScreenshotsFolder) && Directory.Exists(ScreenshotsFolder)
                ? ScreenshotsFolder
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        if (dlg.ShowDialog() == true)
        {
            ScreenshotsFolder = dlg.FolderName;
        }
    }

    private static string DefaultScreenshotsFolder()
    {
        var roberts = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Pictures", "Roberts Space Industries", "ScreenShots");
        return Directory.Exists(roberts) ? roberts : "";
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

            var freshFolder = _prefs.ScreenshotsFolder ?? DefaultScreenshotsFolder();
            if (!string.Equals(ScreenshotsFolder, freshFolder, StringComparison.OrdinalIgnoreCase))
            {
                _isHydrating = false;
                ScreenshotsFolder = freshFolder;
            }
            UpdateScreenshotsFolderStatus(ScreenshotsFolder);
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
