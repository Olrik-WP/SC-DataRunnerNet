using System.IO;
using System.Windows;
using DataRunner.App.Services;
using DataRunner.App.ViewModels;
using DataRunner.App.Views;
using DataRunner.Core.Abstractions;
using DataRunner.Ocr;
using DataRunner.UexClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

namespace DataRunner.App;

public partial class App : Application
{
    public static IHost Host { get; private set; } = null!;

    public static T Resolve<T>() where T : notnull => Host.Services.GetRequiredService<T>();

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var logsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SC-DataRunnerNet", "logs");
        Directory.CreateDirectory(logsDir);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Debug()
            .WriteTo.File(
                Path.Combine(logsDir, "app-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14)
            .CreateLogger();

        Host = Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .UseSerilog()
            .ConfigureServices((_, services) =>
            {
                services.AddUexClient();
                services.AddPaddleOcrPipeline();

                services.AddSingleton<INavigationService, NavigationService>();
                services.AddSingleton<IDialogService, DialogService>();
                services.AddSingleton<OcrCoordinator>();
                services.AddSingleton<ScreenshotFolderWatcher>();
                services.AddHostedService(sp => sp.GetRequiredService<ScreenshotFolderWatcher>());

                services.AddSingleton<MainViewModel>();
                services.AddSingleton<InboxViewModel>();
                services.AddSingleton<SettingsViewModel>();
                services.AddSingleton<HistoryViewModel>();
                services.AddSingleton<TargetsViewModel>();
                services.AddSingleton<FirstRunWizardViewModel>();
                services.AddTransient<ScreenshotEditViewModel>();

                services.AddSingleton<MainWindow>();
            })
            .Build();

        // CRITICAL ORDERING: hydrate prefs and persisted state BEFORE Host.StartAsync()
        // so that hosted services (notably ScreenshotFolderWatcher) and singletons
        // (notably SettingsViewModel) read the user's persisted values, not the
        // defaults. If we ran StartAsync first, the watcher would resolve
        // SettingsViewModel.ScreenshotsFolder = "<Roberts default>" instead of
        // the user's actual folder, and miss every screenshot until the user
        // re-saved settings.
        await HydratePersistedStateAsync();

        await Host.StartAsync();

        // Wire the inbox -> coordinator hand-off (broken via property to avoid
        // the obvious circular DI dependency: coordinator <-> inbox).
        var coordinator = Host.Services.GetRequiredService<OcrCoordinator>();
        var inbox = Host.Services.GetRequiredService<InboxViewModel>();
        inbox.OnImportRequested = path => coordinator.EnqueueAndProcess(path);

        // Wire the inbox -> watcher hand-off so the user can manually trigger
        // a folder rescan from the UI (defensive recovery if a FileSystemWatcher
        // event was missed). Same circular-dependency reasoning as above.
        var watcher = Host.Services.GetRequiredService<ScreenshotFolderWatcher>();
        inbox.OnRescanRequested = window => watcher.RescanAsync(window);

        await EnsureCatalogReadyAsync();

        var secretStore = Host.Services.GetRequiredService<ISecretKeyStore>();
        var hasKey = await secretStore.HasKeyAsync();
        var hasBearer = await secretStore.HasBearerTokenAsync();

        var mainWindow = Host.Services.GetRequiredService<MainWindow>();
        var mainVm = Host.Services.GetRequiredService<MainViewModel>();
        mainWindow.DataContext = mainVm;

        // Trigger the first-run wizard if EITHER credential is missing —
        // both are required by UEX for /data_submit to succeed.
        if (!hasKey || !hasBearer)
        {
            mainVm.NeedsFirstRun = true;
        }

        mainWindow.Show();
    }

    /// <summary>
    /// Loads anything that lives on disk and that singletons resolved DURING
    /// <see cref="IHost.StartAsync"/> would otherwise see in their default state.
    /// Must run BEFORE <see cref="IHost.StartAsync"/>.
    /// </summary>
    private static async Task HydratePersistedStateAsync()
    {
        try
        {
            var prefs = Host.Services.GetRequiredService<IAppPreferences>();
            await prefs.LoadAsync();

            var history = Host.Services.GetRequiredService<ISubmissionHistory>();
            await history.InitializeAsync();
        }
        catch (Exception ex)
        {
            Host.Services.GetRequiredService<ILogger<App>>()
                .LogWarning(ex, "Persisted-state hydration failed; defaults will be used.");
        }
    }

    private static async Task EnsureCatalogReadyAsync()
    {
        try
        {
            var catalog = Host.Services.GetRequiredService<ICatalogProvider>();
            if (catalog.Commodities.Count == 0)
            {
                await catalog.RefreshAsync(force: false);
            }
        }
        catch (Exception ex)
        {
            Host.Services.GetRequiredService<ILogger<App>>()
                .LogWarning(ex, "Catalog warm-up failed; UI will retry on demand.");
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        try
        {
            await Host.StopAsync(TimeSpan.FromSeconds(3));
            Host.Dispose();
        }
        finally
        {
            Log.CloseAndFlush();
            base.OnExit(e);
        }
    }
}
