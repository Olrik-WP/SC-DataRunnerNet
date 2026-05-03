using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DataRunner.App.Services;

namespace DataRunner.App.ViewModels;

/// <summary>
/// Top-level shell view model. Owns navigation state and exposes the side-bar items.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly INavigationService _nav;

    [ObservableProperty] private string _title = "SC DataRunner";
    [ObservableProperty] private string _selectedNavTag = "inbox";
    [ObservableProperty] private bool _needsFirstRun;

    public InboxViewModel Inbox { get; }
    public SettingsViewModel Settings { get; }
    public HistoryViewModel History { get; }
    public TargetsViewModel Targets { get; }
    public FirstRunWizardViewModel FirstRun { get; }
    public OcrCoordinator Ocr { get; }

    public MainViewModel(
        INavigationService nav,
        InboxViewModel inbox,
        SettingsViewModel settings,
        HistoryViewModel history,
        TargetsViewModel targets,
        FirstRunWizardViewModel firstRun,
        OcrCoordinator ocr)
    {
        _nav = nav;
        Inbox = inbox;
        Settings = settings;
        History = history;
        Targets = targets;
        FirstRun = firstRun;
        Ocr = ocr;

        FirstRun.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FirstRunWizardViewModel.IsCompleted) && FirstRun.IsCompleted)
            {
                NeedsFirstRun = false;
                _ = Settings.RefreshAsync();
            }
        };
    }

    [RelayCommand]
    private void NavigateInbox() => SelectedNavTag = "inbox";

    [RelayCommand]
    private void NavigateTargets() => SelectedNavTag = "targets";

    [RelayCommand]
    private void NavigateHistory() => SelectedNavTag = "history";

    [RelayCommand]
    private void NavigateSettings() => SelectedNavTag = "settings";

    [RelayCommand]
    private void DismissFirstRun()
    {
        NeedsFirstRun = false;
        SelectedNavTag = "inbox";
    }
}
