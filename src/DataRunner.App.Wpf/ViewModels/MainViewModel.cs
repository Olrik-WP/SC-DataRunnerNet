using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DataRunner.App.Services;
using DataRunner.Core.Abstractions;

namespace DataRunner.App.ViewModels;

/// <summary>
/// Top-level shell view model. Owns navigation state and exposes the side-bar items.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly INavigationService _nav;
    private IAppPreferences? _prefs;

    [ObservableProperty] private string _title = "SC DataRunner";
    [ObservableProperty] private string _selectedNavTag = "inbox";
    [ObservableProperty] private bool _needsFirstRun;

    /// <summary>
    /// When true, the left nav rail shows icon-only buttons in a narrow strip
    /// so the main content has more width (same collapse pattern as the inbox column).
    /// </summary>
    [ObservableProperty] private bool _isSidebarCollapsed;

    public InboxViewModel Inbox { get; }
    public SettingsViewModel Settings { get; }
    public HistoryViewModel History { get; }
    public TargetsViewModel Targets { get; }
    public RoutesViewModel Routes { get; }
    public DiagnosticsViewModel Diagnostics { get; }
    public FirstRunWizardViewModel FirstRun { get; }
    public UpdateViewModel Updates { get; }
    public OcrCoordinator Ocr { get; }

    public MainViewModel(
        INavigationService nav,
        InboxViewModel inbox,
        SettingsViewModel settings,
        HistoryViewModel history,
        TargetsViewModel targets,
        RoutesViewModel routes,
        DiagnosticsViewModel diagnostics,
        FirstRunWizardViewModel firstRun,
        UpdateViewModel updates,
        OcrCoordinator ocr)
    {
        _nav = nav;
        Inbox = inbox;
        Settings = settings;
        History = history;
        Targets = targets;
        Routes = routes;
        Diagnostics = diagnostics;
        FirstRun = firstRun;
        Updates = updates;
        Ocr = ocr;

        FirstRun.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FirstRunWizardViewModel.IsCompleted) && FirstRun.IsCompleted)
            {
                NeedsFirstRun = false;
                _ = Settings.RefreshAsync();
            }
        };

        // When the user picks "Open trade routes from this terminal in DataRunner"
        // from the Targets view's context menu, jump to the Routes tab AND
        // pre-fill the origin so the page is immediately useful.
        Targets.OpenRoutesInAppRequested += async (_, idTerminal) =>
        {
            SelectedNavTag = "routes";
            await Routes.PreFillFromTerminalAsync(idTerminal).ConfigureAwait(false);
        };
    }

    /// <summary>Wired at startup so the nav collapse toggle survives restarts.</summary>
    public void AttachPreferences(IAppPreferences prefs)
    {
        _prefs = prefs;
        IsSidebarCollapsed = prefs.SidebarCollapsed;
    }

    partial void OnIsSidebarCollapsedChanged(bool value)
    {
        if (_prefs is null) return;
        _prefs.SidebarCollapsed = value;
        _ = _prefs.SaveAsync();
    }

    [RelayCommand]
    private void ToggleSidebarCollapsed() => IsSidebarCollapsed = !IsSidebarCollapsed;

    [RelayCommand]
    private void NavigateInbox() => SelectedNavTag = "inbox";

    [RelayCommand]
    private void NavigateTargets() => SelectedNavTag = "targets";

    [RelayCommand]
    private void NavigateRoutes() => SelectedNavTag = "routes";

    [RelayCommand]
    private void NavigateHistory() => SelectedNavTag = "history";

    [RelayCommand]
    private void NavigateDiagnostics() => SelectedNavTag = "diagnostics";

    [RelayCommand]
    private void NavigateSettings() => SelectedNavTag = "settings";

    [RelayCommand]
    private void DismissFirstRun()
    {
        NeedsFirstRun = false;
        SelectedNavTag = "inbox";
    }
}
