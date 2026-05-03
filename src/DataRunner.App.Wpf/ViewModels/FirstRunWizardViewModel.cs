using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DataRunner.App.Services;
using DataRunner.Core.Abstractions;

namespace DataRunner.App.ViewModels;

/// <summary>
/// Four-step welcome wizard shown on first launch.
///   Step 0 - Welcome / what this app does
///   Step 1 - Paste UEX user secret-key (from Account page)
///   Step 2 - Paste UEX app bearer token (from /api/apps)
///   Step 3 - Pick screenshot folder + done
/// </summary>
public sealed partial class FirstRunWizardViewModel : ObservableObject
{
    private readonly ISecretKeyStore _secretStore;
    private readonly IAppPreferences _prefs;
    private readonly IDialogService _dialog;

    [ObservableProperty] private int _currentStep = 0;
    [ObservableProperty] private string _secretKeyInput = "";
    [ObservableProperty] private string _bearerTokenInput = "";
    [ObservableProperty] private string _screenshotsFolder = "";
    [ObservableProperty] private bool _isCompleted;

    public int TotalSteps => 4;

    public bool CanGoNext => CurrentStep switch
    {
        0 => true,
        1 => !string.IsNullOrWhiteSpace(SecretKeyInput),
        2 => !string.IsNullOrWhiteSpace(BearerTokenInput),
        3 => true,
        _ => false,
    };

    public bool CanGoBack => CurrentStep > 0;
    public bool IsLastStep => CurrentStep == TotalSteps - 1;

    public FirstRunWizardViewModel(ISecretKeyStore secretStore, IAppPreferences prefs, IDialogService dialog)
    {
        _secretStore = secretStore;
        _prefs = prefs;
        _dialog = dialog;

        // Pre-fill with whatever the user already has in prefs (in case they re-open
        // the wizard), or fall back to the standard SC location if it exists.
        if (!string.IsNullOrWhiteSpace(_prefs.ScreenshotsFolder))
        {
            ScreenshotsFolder = _prefs.ScreenshotsFolder!;
        }
        else
        {
            var defaultFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Pictures", "Roberts Space Industries", "ScreenShots");
            if (Directory.Exists(defaultFolder)) ScreenshotsFolder = defaultFolder;
        }
    }

    [RelayCommand]
    private void BrowseFolder()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Pick the folder Star Citizen writes screenshots to (F12)",
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
        else if (CurrentStep == 2)
        {
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
