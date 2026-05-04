using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DataRunner.App.Services;
using DataRunner.Core.Abstractions;

namespace DataRunner.App.ViewModels;

/// <summary>
/// Welcome wizard shown on first launch. The number of steps is dynamic:
///   - When the build embeds an app bearer token (official CI release), the
///     wizard has THREE steps: welcome / secret-key / folder.
///   - When no token is embedded (self-build / dev), it has FOUR steps:
///     welcome / secret-key / app-bearer-token / folder.
///
/// Step indices stay stable for binding simplicity: step 2 is ALWAYS the
/// bearer step, just skipped (collapsed) when the embedded token is present.
/// </summary>
public sealed partial class FirstRunWizardViewModel : ObservableObject
{
    private readonly ISecretKeyStore _secretStore;
    private readonly IAppPreferences _prefs;
    private readonly IBuiltInAppTokenProvider _builtInToken;
    private readonly IDialogService _dialog;

    [ObservableProperty] private int _currentStep = 0;
    [ObservableProperty] private string _secretKeyInput = "";
    [ObservableProperty] private string _bearerTokenInput = "";
    [ObservableProperty] private string _screenshotsFolder = "";
    [ObservableProperty] private bool _isCompleted;

    /// <summary>True when a build-time bearer token is available; the wizard
    /// then skips the bearer step (3 visible steps instead of 4).</summary>
    public bool HasBuiltInBearerToken => _builtInToken.HasToken;

    /// <summary>True when the bearer step should be SHOWN to the user (i.e. no
    /// embedded token available). Bound by the view to swap step 2's content.</summary>
    public bool ShowsBearerStep => !HasBuiltInBearerToken;

    public int TotalSteps => HasBuiltInBearerToken ? 3 : 4;

    public bool CanGoNext => CurrentStep switch
    {
        0 => true,
        1 => !string.IsNullOrWhiteSpace(SecretKeyInput),
        // Step 2 is the bearer step when shown, otherwise the folder step (which
        // doesn't gate on input).
        2 => HasBuiltInBearerToken || !string.IsNullOrWhiteSpace(BearerTokenInput),
        3 => true,
        _ => false,
    };

    public bool CanGoBack => CurrentStep > 0;
    public bool IsLastStep => CurrentStep == TotalSteps - 1;

    // Per-step visibility helpers used by the XAML so the view stays
    // declarative. When a built-in token is available the wizard collapses
    // the bearer step; the folder step then "moves up" to take its slot.
    public bool ShowWelcomeStep => CurrentStep == 0;
    public bool ShowSecretKeyStep => CurrentStep == 1;
    public bool ShowBearerInputStep => CurrentStep == 2 && !HasBuiltInBearerToken;
    public bool ShowFolderStep => (CurrentStep == 2 && HasBuiltInBearerToken)
                                 || (CurrentStep == 3 && !HasBuiltInBearerToken);

    /// <summary>"Step 2/3" or "Step 2/4" depending on whether the bearer step
    /// is shown. Bound by the view to render the step counter consistently.</summary>
    public string SecretKeyStepLabel => HasBuiltInBearerToken ? "1/2" : "1/3";
    public string FolderStepLabel => HasBuiltInBearerToken ? "2/2" : "3/3";

    public FirstRunWizardViewModel(
        ISecretKeyStore secretStore,
        IAppPreferences prefs,
        IBuiltInAppTokenProvider builtInToken,
        IDialogService dialog)
    {
        _secretStore = secretStore;
        _prefs = prefs;
        _builtInToken = builtInToken;
        _dialog = dialog;

        // Pre-fill with whatever the user already has in prefs (in case they re-open
        // the wizard), or fall back to the standard SC location if it exists.
        if (!string.IsNullOrWhiteSpace(_prefs.ScreenshotsFolder))
        {
            ScreenshotsFolder = _prefs.ScreenshotsFolder!;
        }
        else
        {
            ScreenshotsFolder = FindScScreenshotsFolder() ?? "";
        }
    }

    [RelayCommand]
    private void BrowseFolder()
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

    partial void OnCurrentStepChanged(int value)
    {
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(IsLastStep));
        OnPropertyChanged(nameof(ShowWelcomeStep));
        OnPropertyChanged(nameof(ShowSecretKeyStep));
        OnPropertyChanged(nameof(ShowBearerInputStep));
        OnPropertyChanged(nameof(ShowFolderStep));
    }

    partial void OnSecretKeyInputChanged(string value) => OnPropertyChanged(nameof(CanGoNext));
    partial void OnBearerTokenInputChanged(string value) => OnPropertyChanged(nameof(CanGoNext));

    [RelayCommand]
    private void GoBack()
    {
        if (CanGoBack) CurrentStep--;
    }

    [RelayCommand]
    private async Task GoNextAsync()
    {
        if (CurrentStep == 1)
        {
            try { await _secretStore.SetAsync(SecretKeyInput.Trim()); }
            catch (Exception ex) { _dialog.ShowError("Could not save secret-key", ex.Message); return; }
            SecretKeyInput = "";
        }
        else if (CurrentStep == 2 && !HasBuiltInBearerToken)
        {
            // Bearer step is only meaningful when no token was embedded at
            // build time; otherwise step 2 is already the folder step.
            try { await _secretStore.SetBearerTokenAsync(BearerTokenInput.Trim()); }
            catch (Exception ex) { _dialog.ShowError("Could not save bearer token", ex.Message); return; }
            BearerTokenInput = "";
        }

        if (IsLastStep)
        {
            // Persist the screenshots folder picked at step 3. Without this the
            // wizard "Done" button silently dropped the value and the user had
            // to re-enter it in Settings (which itself was unreliable, see
            // SettingsViewModel.OnScreenshotsFolderChanged).
            try
            {
                _prefs.ScreenshotsFolder = string.IsNullOrWhiteSpace(ScreenshotsFolder) ? null : ScreenshotsFolder.Trim();
                await _prefs.SaveAsync();
            }
            catch (Exception ex)
            {
                _dialog.ShowError("Could not save screenshots folder", ex.Message);
                return;
            }

            IsCompleted = true;
            return;
        }
        CurrentStep++;
    }

    /// <summary>
    /// Probes several well-known locations for the SC Screenshots folder.
    /// SC's screenshot path depends on the install drive chosen by the user,
    /// so we check a few common roots. Returns the first one that exists
    /// or <c>null</c> when none is found (the user will have to Browse).
    /// </summary>
    private static string? FindScScreenshotsFolder()
    {
        // Standard Roberts launcher path under user profile
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new[]
        {
            Path.Combine(userProfile, "Pictures", "Roberts Space Industries", "ScreenShots"),
        };

        // Probe every fixed drive for <root>\StarCitizen\LIVE\Screenshots
        // and <root>\Roberts Space Industries\StarCitizen\LIVE\Screenshots
        // (the two most common install layouts).
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
            var root = drive.RootDirectory.FullName;
            // D:\StarCitizen\LIVE\Screenshots  (or C:\, E:\, ...)
            var p1 = Path.Combine(root, "StarCitizen", "LIVE", "Screenshots");
            if (Directory.Exists(p1)) return p1;
            // D:\Program Files\Roberts Space Industries\StarCitizen\LIVE\Screenshots
            var p2 = Path.Combine(root, "Program Files", "Roberts Space Industries", "StarCitizen", "LIVE", "Screenshots");
            if (Directory.Exists(p2)) return p2;
            // D:\Jeux\StarCitizen\LIVE\Screenshots  (French installs)
            var p3 = Path.Combine(root, "Jeux", "StarCitizen", "LIVE", "Screenshots");
            if (Directory.Exists(p3)) return p3;
            // D:\Games\StarCitizen\LIVE\Screenshots
            var p4 = Path.Combine(root, "Games", "StarCitizen", "LIVE", "Screenshots");
            if (Directory.Exists(p4)) return p4;
        }

        foreach (var c in candidates)
        {
            if (Directory.Exists(c)) return c;
        }

        return null;
    }

    [RelayCommand]
    private void OpenUexAccountPage()
        => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "https://uexcorp.space/account",
            UseShellExecute = true,
        });

    [RelayCommand]
    private void OpenUexAppsPage()
        => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "https://uexcorp.space/api/apps",
            UseShellExecute = true,
        });
}
