using System.IO;
using System.Reflection;
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
using Velopack;

namespace DataRunner.App;

public partial class App : Application
{
    public static IHost Host { get; private set; } = null!;

    public static T Resolve<T>() where T : notnull => Host.Services.GetRequiredService<T>();

    /// <summary>
    /// Custom WPF entry point.
    /// <para>
    /// We hand-roll Main() (instead of letting the SDK auto-generate it from
    /// App.xaml) so <see cref="VelopackApp.Build"/> can run BEFORE any WPF
    /// machinery. That window is non-negotiable: Velopack uses it to handle
    /// silent first-install / update / uninstall hooks that must NOT spawn
    /// a UI. If we let WPF start first, those hooks would briefly flash the
    /// main window during installation.
    /// </para>
    /// </summary>
    [STAThread]
    public static void Main(string[] args)
    {
        // Surface unhandled crashes from the Velopack install hooks into the
        // standard log file (logs are a folder under LocalAppData; see below).
        var logsDir = ResolveLogsDir();
        Directory.CreateDirectory(logsDir);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Debug()
            .WriteTo.File(
                Path.Combine(logsDir, "app-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14)
            .CreateLogger();

        try
        {
            // Velopack hooks (install / update / uninstall) terminate the
            // process on their own when triggered by the installer; if we
            // were *not* triggered by the installer, control falls through
            // and we boot the app normally.
            VelopackApp.Build()
                .OnFirstRun(_ => Log.Information("Velopack: first-run hook fired (install completed)."))
                .OnAfterUpdateFastCallback(v => Log.Information("Velopack: post-update hook for {Version}.", v))
                .OnBeforeUninstallFastCallback(v => Log.Information("Velopack: pre-uninstall hook for {Version}.", v))
                .Run();

            var app = new App();
            app.InitializeComponent();
            app.Run();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Fatal error during startup.");
            throw;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    private static string ResolveLogsDir() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SC-DataRunnerNet", "logs");

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        Host = Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .UseSerilog()
            .ConfigureServices((_, services) =>
            {
                services.AddUexClient();
                services.AddPaddleOcrPipeline();

                services.AddSingleton<INavigationService, NavigationService>();
                services.AddSingleton<IDialogService, DialogService>();
                services.AddSingleton<IUpdateService, VelopackUpdateService>();
                services.AddSingleton<OcrCoordinator>();
                services.AddSingleton<ScreenshotFolderWatcher>();
                services.AddHostedService(sp => sp.GetRequiredService<ScreenshotFolderWatcher>());

                services.AddSingleton<MainViewModel>();
                services.AddSingleton<InboxViewModel>();
                services.AddSingleton<SettingsViewModel>();
                services.AddSingleton<HistoryViewModel>();
                services.AddSingleton<TargetsViewModel>();
                services.AddSingleton<UpdateViewModel>();
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

        var settings = Host.Services.GetRequiredService<SettingsViewModel>();
        inbox.GetScreenshotsFolderPath = () => settings.ScreenshotsFolder;

        // Hand the prefs reference to the inbox so its collapse toggle can
        // persist across app restarts. Same pattern (delegate / property)
        // we use for OnImportRequested / OnRescanRequested to avoid a circular
        // DI dependency: the inbox is a singleton resolved BEFORE prefs hydrate.
        var prefs = Host.Services.GetRequiredService<IAppPreferences>();
        inbox.AttachPreferences(prefs);

        await EnsureCatalogReadyAsync();

        var secretStore = Host.Services.GetRequiredService<ISecretKeyStore>();
        var builtInToken = Host.Services.GetRequiredService<IBuiltInAppTokenProvider>();
        var hasKey = await secretStore.HasKeyAsync();
        var hasUserBearer = await secretStore.HasBearerTokenAsync();

        var mainWindow = Host.Services.GetRequiredService<MainWindow>();
        var mainVm = Host.Services.GetRequiredService<MainViewModel>();
        mainWindow.DataContext = mainVm;

        // Trigger the first-run wizard when the user secret-key is missing,
        // OR when no bearer token is available at all (neither user-provided
        // nor embedded at build time). Official releases ship with an embedded
        // token, so the typical first-run experience is just one secret-key
        // step instead of two.
        var hasAnyBearer = hasUserBearer || builtInToken.HasToken;
        if (!hasKey || !hasAnyBearer)
        {
            mainVm.NeedsFirstRun = true;
        }

        mainWindow.Show();

        // Fire-and-forget background update probe. Errors are logged inside
        // the service; we never block startup or interrupt the user.
        _ = Host.Services.GetRequiredService<UpdateViewModel>()
            .CheckForUpdatesSilentlyAsync();
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

    /// <summary>
    /// Best-effort: returns the SemVer string baked into the assembly by
    /// Directory.Build.props at compile time. Falls back to "0.0.0" on dev
    /// builds where attributes have been stripped.
    /// </summary>
    public static string GetAppVersion()
    {
        var asm = Assembly.GetExecutingAssembly();
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            // Strip the "+<sha>" suffix when present so the UI shows "1.2.3".
            var plus = info.IndexOf('+');
            return plus > 0 ? info[..plus] : info;
        }
        return asm.GetName().Version?.ToString(3) ?? "0.0.0";
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
