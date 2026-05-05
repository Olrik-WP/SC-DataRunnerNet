using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DataRunner.App.Services;
using DataRunner.Core.Abstractions;
using DataRunner.Core.Models;

namespace DataRunner.App.ViewModels;

/// <summary>
/// Welcome wizard shown on first launch. Five logical steps total, with a
/// dynamic count depending on whether the build embeds a UEX app bearer
/// token (official CI release) or not (self-built / dev):
///
///   index | shown when         | content
///   ------|--------------------|-------------------------------------------
///   0     | always             | Welcome
///   1     | always             | User secret-key
///   2     | NO embedded token  | App bearer token (skipped when embedded)
///   3     | always             | Submission mode (is_production toggle)
///   4     | always             | Screenshots folders (LIVE + optional PTU)
///
/// When the bearer step is skipped, indices 3 and 4 effectively shift down
/// by one in the visible counter — the per-step <c>ShowXxxStep</c> helpers
/// take care of that, the rest of the codebase only sees the absolute
/// index ranges 0..4.
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
    [ObservableProperty] private string _liveScreenshotsFolder = "";
    [ObservableProperty] private string _ptuScreenshotsFolder = "";
    [ObservableProperty] private bool _defaultIsProduction = true;
    [ObservableProperty] private bool _isCompleted;

    /// <summary>True when a build-time bearer token is available; the wizard
    /// then skips the bearer step (one less visible step).</summary>
    public bool HasBuiltInBearerToken => _builtInToken.HasToken;

    /// <summary>True when the bearer step should be SHOWN to the user (i.e. no
    /// embedded token available). Bound by the view to swap step 2's content.</summary>
    public bool ShowsBearerStep => !HasBuiltInBearerToken;

    /// <summary>4 visible steps when the bearer is embedded, 5 otherwise.</summary>
    public int TotalSteps => HasBuiltInBearerToken ? 4 : 5;

    public bool CanGoNext => CurrentStep switch
    {
        0 => true,
        1 => !string.IsNullOrWhiteSpace(SecretKeyInput),
        // Step 2 is the bearer step when shown, otherwise the submission-mode
        // step (which doesn't gate on input).
        2 => HasBuiltInBearerToken || !string.IsNullOrWhiteSpace(BearerTokenInput),
        3 => true,   // Submission mode (or folder when bearer is embedded)
        4 => true,   // Folder (only reachable on self-build path)
        _ => false,
    };

    public bool CanGoBack => CurrentStep > 0;
    public bool IsLastStep => CurrentStep == TotalSteps - 1;

    // Per-step visibility helpers used by the XAML so the view stays
    // declarative. When a built-in token is available the wizard collapses
    // the bearer step; later steps then "move up" by one slot.
    public bool ShowWelcomeStep => CurrentStep == 0;
    public bool ShowSecretKeyStep => CurrentStep == 1;
    public bool ShowBearerInputStep => CurrentStep == 2 && !HasBuiltInBearerToken;
    public bool ShowSubmissionModeStep => (CurrentStep == 2 && HasBuiltInBearerToken)
                                       || (CurrentStep == 3 && !HasBuiltInBearerToken);
    public bool ShowFolderStep => (CurrentStep == 3 && HasBuiltInBearerToken)
                                 || (CurrentStep == 4 && !HasBuiltInBearerToken);

    /// <summary>Step counter labels — show "X/N" where N matches <see cref="TotalSteps"/> (minus the welcome step).</summary>
    public string SecretKeyStepLabel => HasBuiltInBearerToken ? "1/3" : "1/4";
    public string SubmissionModeStepLabel => HasBuiltInBearerToken ? "2/3" : "3/4";
    public string FolderStepLabel => HasBuiltInBearerToken ? "3/3" : "4/4";

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

        // Hydrate from prefs first (covers users re-opening the wizard), then
        // fall back to the standard SC install layouts. PTU folder defaults
        // to empty when nothing is found — it's optional.
        LiveScreenshotsFolder = !string.IsNullOrWhiteSpace(_prefs.LiveScreenshotsFolder)
            ? _prefs.LiveScreenshotsFolder!
            : (FindScScreenshotsFolder(GameBranch.Live) ?? "");
        PtuScreenshotsFolder = !string.IsNullOrWhiteSpace(_prefs.PtuScreenshotsFolder)
            ? _prefs.PtuScreenshotsFolder!
            : (FindScScreenshotsFolder(GameBranch.Ptu) ?? "");

        DefaultIsProduction = _prefs.DefaultIsProduction;
    }

    [RelayCommand]
    private void BrowseLiveFolder() => BrowseFolder(GameBranch.Live);

    [RelayCommand]
    private void BrowsePtuFolder() => BrowseFolder(GameBranch.Ptu);

    private void BrowseFolder(GameBranch branch)
    {
        var current = branch == GameBranch.Live ? LiveScreenshotsFolder : PtuScreenshotsFolder;
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = branch == GameBranch.Live
                ? "Pick the LIVE Star Citizen screenshots folder"
                : "Pick the PTU Star Citizen screenshots folder (optional)",
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

    partial void OnCurrentStepChanged(int value)
    {
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(IsLastStep));
        OnPropertyChanged(nameof(ShowWelcomeStep));
        OnPropertyChanged(nameof(ShowSecretKeyStep));
        OnPropertyChanged(nameof(ShowBearerInputStep));
        OnPropertyChanged(nameof(ShowSubmissionModeStep));
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
            // Persist all wizard outputs (folders + submission-mode default).
            // Without this the "Done" button silently dropped the values and
            // the user had to re-enter them in Settings.
            try
            {
                _prefs.LiveScreenshotsFolder = string.IsNullOrWhiteSpace(LiveScreenshotsFolder)
                    ? null : LiveScreenshotsFolder.Trim();
                _prefs.PtuScreenshotsFolder = string.IsNullOrWhiteSpace(PtuScreenshotsFolder)
                    ? null : PtuScreenshotsFolder.Trim();
                _prefs.DefaultIsProduction = DefaultIsProduction;
                await _prefs.SaveAsync();
            }
            catch (Exception ex)
            {
                _dialog.ShowError("Could not save preferences", ex.Message);
                return;
            }

            IsCompleted = true;
            return;
        }
        CurrentStep++;
    }

    /// <summary>
    /// Probes several well-known locations for the SC Screenshots folder
    /// matching the requested branch. SC's screenshot path depends on the
    /// install drive chosen by the user, so we check a few common roots.
    /// Returns the first one that exists or <c>null</c> when none is found
    /// (the user will have to Browse manually).
    /// </summary>
    private static string? FindScScreenshotsFolder(GameBranch branch)
    {
        var subfolder = branch == GameBranch.Ptu ? "PTU" : "LIVE";

        // The Roberts launcher pictures path is shared by all branches; only
        // surface it for LIVE since that's the most common starting point on
        // a fresh install. PTU users almost always have a per-install folder.
        if (branch == GameBranch.Live)
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var roberts = Path.Combine(userProfile, "Pictures", "Roberts Space Industries", "ScreenShots");
            if (Directory.Exists(roberts)) return roberts;
        }

        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
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
