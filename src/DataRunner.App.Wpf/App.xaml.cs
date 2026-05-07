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

    private static Window? _startupSplash;

    /// <summary>
    /// Borderless splash at 50% of the embedded PNG. Shown before <see cref="OnStartup"/>;
    /// closed when the main window loads (same timing as the built-in WPF splash auto-close).
    /// </summary>
    static App()
    {
        var splash = new SplashWindow();
        _startupSplash = splash;
        splash.Show();
    }

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

    private static void CloseStartupSplash()
    {
        try
        {
            _startupSplash?.Close();
        }
        finally
        {
            _startupSplash = null;
        }
    }

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

                // Smart-split + sequential POST loop for the inbox toolbar's
                // Send batch flow. The payload factory is shared between the
                // submitter (real POST) and the preview view model (so the
                // dialog shows the EXACT JSON the submitter will send). All
                // three are stateless apart from their DI dependencies so
                // singleton lifetimes are fine.
                services.AddSingleton<IBatchPayloadFactory, BatchPayloadFactory>();
                services.AddSingleton<IBatchPlanner, BatchPlanner>();
                services.AddSingleton<IBatchSubmitter, BatchSubmitter>();

                services.AddSingleton<MainViewModel>();
                services.AddSingleton<InboxViewModel>();
                services.AddSingleton<SettingsViewModel>();
                services.AddSingleton<HistoryViewModel>();
                services.AddSingleton<DiagnosticsViewModel>();
                services.AddSingleton<TargetsViewModel>();
                services.AddSingleton<RoutesViewModel>();
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
        var mainVm = Host.Services.GetRequiredService<MainViewModel>();
        inbox.AttachPreferences(prefs);
        mainVm.AttachPreferences(prefs);

        // Wire the smart-split + sequential POST batch send. The closure owns
        // the planner / submitter / dialog dependencies so InboxViewModel can
        // stay decoupled from the submission stack (it's a singleton resolved
        // very early in the DI graph; pulling those services in via the
        // constructor would create a heavy startup chain).
        var batchPlanner = Host.Services.GetRequiredService<IBatchPlanner>();
        var batchSubmitter = Host.Services.GetRequiredService<IBatchSubmitter>();
        var batchPayloadFactory = Host.Services.GetRequiredService<IBatchPayloadFactory>();
        var dialogService = Host.Services.GetRequiredService<IDialogService>();
        var gameVersions = Host.Services.GetRequiredService<IGameVersionsService>();
        inbox.OnSendBatchRequested = async (validatedItems, ct) =>
            await RunBatchSendAsync(validatedItems, ct, batchPlanner, batchSubmitter,
                                    batchPayloadFactory, dialogService, prefs, gameVersions).ConfigureAwait(true);

        await EnsureCatalogReadyAsync();

        var secretStore = Host.Services.GetRequiredService<ISecretKeyStore>();
        var builtInToken = Host.Services.GetRequiredService<IBuiltInAppTokenProvider>();
        var hasKey = await secretStore.HasKeyAsync();
        var hasUserBearer = await secretStore.HasBearerTokenAsync();

        var mainWindow = Host.Services.GetRequiredService<MainWindow>();
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

        mainWindow.Loaded += (_, _) => CloseStartupSplash();
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

            // Pre-load the cached UEX game versions from disk so the editor
            // can pre-fill the GAME VERSION field even if the first network
            // /game_versions request hasn't landed yet (eg. user is offline).
            var gameVersions = Host.Services.GetRequiredService<IGameVersionsService>();
            await gameVersions.LoadFromDiskAsync();
            // Fire-and-forget refresh in the background so subsequent loads
            // see the latest UEX values without blocking startup.
            _ = Task.Run(async () =>
            {
                try { await gameVersions.RefreshAsync(); }
                catch { /* logged inside the service */ }
            });
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
    /// Drives a Send batch run from the inbox to UEX:
    ///   1. Plan the smart-split (per-terminal, latest-wins commodities).
    ///   2. Show the mandatory preview dialog. Cancel here = abort, no POST.
    ///   3. Stream submissions through <see cref="IBatchSubmitter"/>, mirror
    ///      each progress event onto the matching <see cref="InboxItem.Status"/>
    ///      so the inbox cards reflect the live state (Sending → Sent / Failed).
    ///
    /// Lives on App.xaml.cs (not in InboxViewModel) so the inbox VM doesn't
    /// take a constructor dependency on the planner / submitter / dialog
    /// service — those would force the heavy submission stack to resolve
    /// during early app startup.
    /// </summary>
    private static async Task RunBatchSendAsync(
        IReadOnlyList<ViewModels.InboxItem> validatedItems,
        CancellationToken ct,
        IBatchPlanner planner,
        IBatchSubmitter submitter,
        IBatchPayloadFactory payloadFactory,
        IDialogService dialogService,
        IAppPreferences prefs,
        IGameVersionsService gameVersions)
    {
        var logger = Host.Services.GetRequiredService<ILogger<App>>();

        var plan = planner.Plan(validatedItems);
        if (plan.Submissions.Count == 0)
        {
            dialogService.ShowInfo("Nothing to send",
                "After the smart-split pass, no submissions remain to be sent.");
            return;
        }

        // Snapshot the game-version values BEFORE the dialog so the JSON
        // preview reflects the same `game_version` the submitter will use.
        // /game_versions can refresh mid-run; freezing it here keeps the
        // preview <-> network bodies byte-identical.
        string? liveVersion = null;
        string? ptuVersion = null;
        try { liveVersion = await gameVersions.ResolveAsync(Core.Models.GameBranch.Live, ct).ConfigureAwait(true); }
        catch (Exception ex) { logger.LogWarning(ex, "Could not resolve LIVE game version for batch send."); }
        try { ptuVersion = await gameVersions.ResolveAsync(Core.Models.GameBranch.Ptu, ct).ConfigureAwait(true); }
        catch (Exception ex) { logger.LogWarning(ex, "Could not resolve PTU game version for batch send."); }

        var options = new BatchOptions(
            DefaultIsProduction: prefs.DefaultIsProduction,
            LiveGameVersion: liveVersion,
            PtuGameVersion: ptuVersion);

        // Mandatory preview — cancellation here means the user changed their
        // mind, so we just bail without touching any item's status.
        var previewVm = new ViewModels.BatchPreviewViewModel(plan, options, payloadFactory, prefs);
        var confirmed = await dialogService.ShowBatchPreviewAsync(previewVm).ConfigureAwait(true);
        if (!confirmed) return;

        try
        {
            await foreach (var progress in submitter.RunAsync(plan, options, ct).ConfigureAwait(true))
            {
                ApplyBatchProgressToInbox(progress);
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Batch send cancelled by user; remaining items kept at their last status.");
            // Items that never started are still Validated. The currently
            // Sending one (if any) is finalised by the submitter's foreach
            // before the OperationCanceledException bubbles.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Batch send loop crashed unexpectedly.");
            dialogService.ShowError("Batch send failed",
                $"The batch send loop crashed unexpectedly:\n\n{ex.Message}\n\nIndividual items keep their last known status; rerun via Retry failed if you want to ship the survivors.");
        }
    }

    /// <summary>
    /// Mirrors a per-item <see cref="BatchProgress"/> event onto the matching
    /// <see cref="ViewModels.InboxItem"/> so the inbox cards show the live
    /// progression of the batch in real time.
    /// </summary>
    private static void ApplyBatchProgressToInbox(BatchProgress progress)
    {
        var item = progress.Item.SourceItem;
        switch (progress.Phase)
        {
            case BatchProgressPhase.Sending:
                item.Status = ViewModels.InboxStatus.Sending;
                item.StatusReason = "POST in flight...";
                break;

            case BatchProgressPhase.Skipped:
                // No-op submissions (everything deduped away on a stale
                // screenshot of an already-sent terminal). We tag them as
                // Sent so the user can prune them with Remove all sent.
                item.Status = ViewModels.InboxStatus.Sent;
                item.StatusReason = progress.SkipReason ?? "Skipped (no commodities to send).";
                break;

            case BatchProgressPhase.Done:
                var outcome = progress.Outcome!;
                if (outcome.Ok)
                {
                    item.Status = ViewModels.InboxStatus.Sent;
                    item.StatusReason = outcome.DeletedFiles > 0
                        ? $"Sent ({outcome.HttpStatusCode}). Cleaned up {outcome.DeletedFiles} screenshot(s)."
                        : $"Sent ({outcome.HttpStatusCode}).";
                }
                else
                {
                    item.Status = ViewModels.InboxStatus.Failed;
                    var apiTag = string.IsNullOrWhiteSpace(outcome.ApiStatus) ? "" : $"[{outcome.ApiStatus}] ";
                    item.StatusReason = $"Failed ({outcome.HttpStatusCode}). {apiTag}{outcome.Message}";
                }
                break;
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
