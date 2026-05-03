using System.IO;
using DataRunner.App.ViewModels;
using DataRunner.Core.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DataRunner.App.Services;

/// <summary>
/// Hosted service that watches the configured Star Citizen screenshots folder and
/// auto-imports new images into the OCR pipeline. Reacts to settings changes:
/// when the user picks a new folder in <see cref="SettingsViewModel"/>, the watcher
/// is recreated on the new path with no restart.
/// </summary>
public sealed class ScreenshotFolderWatcher : IHostedService, IDisposable
{
    private static readonly string[] AllowedExtensions = [".png", ".jpg", ".jpeg", ".bmp"];
    private static readonly TimeSpan StableSizeWindow = TimeSpan.FromMilliseconds(700);
    private static readonly TimeSpan StableSizePollInterval = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan StableSizeTimeout = TimeSpan.FromSeconds(8);

    /// <summary>
    /// Defensive window for the initial-scan pass: any file modified during
    /// the last <c>InitialScanWindow</c> at watcher start is enqueued, in case
    /// the user dropped screenshots BEFORE the watcher was active (or while we
    /// were resolving DI / loading prefs).
    /// </summary>
    private static readonly TimeSpan InitialScanWindow = TimeSpan.FromMinutes(5);

    private readonly OcrCoordinator _coordinator;
    private readonly SettingsViewModel _settings;
    private readonly ISubmissionHistory _history;
    private readonly ILogger<ScreenshotFolderWatcher> _logger;

    private FileSystemWatcher? _fsw;
    private string? _currentFolder;
    private CancellationTokenSource? _stopCts;

    public ScreenshotFolderWatcher(
        OcrCoordinator coordinator,
        SettingsViewModel settings,
        ISubmissionHistory history,
        ILogger<ScreenshotFolderWatcher> logger)
    {
        _coordinator = coordinator;
        _settings = settings;
        _history = history;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _stopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _settings.PropertyChanged += OnSettingsChanged;
        Reconfigure(_settings.ScreenshotsFolder);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _settings.PropertyChanged -= OnSettingsChanged;
        DisposeWatcher();
        _stopCts?.Cancel();
        _stopCts?.Dispose();
        _stopCts = null;
        return Task.CompletedTask;
    }

    private void OnSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.ScreenshotsFolder))
        {
            Reconfigure(_settings.ScreenshotsFolder);
        }
    }

    private void Reconfigure(string folder)
    {
        DisposeWatcher();

        if (string.IsNullOrWhiteSpace(folder))
        {
            _logger.LogInformation("Screenshot folder watcher: no folder configured.");
            return;
        }
        if (!Directory.Exists(folder))
        {
            _logger.LogWarning("Screenshot folder does not exist: {Folder}", folder);
            return;
        }

        _currentFolder = folder;
        var fsw = new FileSystemWatcher(folder)
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = false,
        };
        fsw.Created += OnFile;
        fsw.Renamed += (s, e) => OnFile(s, e);
        fsw.EnableRaisingEvents = true;

        _fsw = fsw;
        _logger.LogInformation("Watching screenshot folder: {Folder}", folder);

        // Defensive initial scan: pick up files dropped during the brief window
        // between the user taking a screenshot and the watcher being live.
        var ct = _stopCts?.Token ?? CancellationToken.None;
        _ = Task.Run(() => InitialScanAsync(folder, InitialScanWindow, ct), ct);
    }

    private async Task InitialScanAsync(string folder, TimeSpan window, CancellationToken ct)
    {
        try
        {
            var alreadySent = await GetAlreadySentNamesAsync(ct).ConfigureAwait(false);
            var cutoff = DateTime.UtcNow - window;
            var allRecent = new DirectoryInfo(folder)
                .EnumerateFiles()
                .Where(f => IsAllowed(f.FullName) && f.LastWriteTimeUtc >= cutoff)
                .OrderBy(f => f.LastWriteTimeUtc)
                .ToList();

            var files = allRecent.Where(f => !alreadySent.Contains(f.Name)).ToList();
            var skipped = allRecent.Count - files.Count;

            if (files.Count == 0)
            {
                if (skipped > 0)
                {
                    _logger.LogInformation(
                        "Initial scan: nothing to enqueue ({Skipped} file(s) skipped — already submitted to UEX).",
                        skipped);
                }
                return;
            }

            _logger.LogInformation(
                "Initial scan: enqueueing {Count} screenshot(s) from the last {Mins} min in {Folder} (skipped {Skipped} already-submitted).",
                files.Count, (int)window.TotalMinutes, folder, skipped);

            foreach (var f in files)
            {
                ct.ThrowIfCancellationRequested();
                await EnqueueWhenStableAsync(f.FullName, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Initial folder scan failed for {Folder}", folder);
        }
    }

    /// <summary>
    /// Best-effort load of the "already submitted with success" set. On error
    /// we return an empty set so the scan still proceeds (the worst case is
    /// re-OCRing a file the user already sent — annoying but not destructive).
    /// </summary>
    private async Task<HashSet<string>> GetAlreadySentNamesAsync(CancellationToken ct)
    {
        try
        {
            return await _history.GetSubmittedSourceImagesAsync(productionOnly: true, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not query SubmissionHistory for sent files; scanning everything.");
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Result of <see cref="RescanAsync"/> — used by the UI to show actionable feedback
    /// (count of files picked up, or why nothing happened).
    /// </summary>
    public sealed record RescanResult(bool RanScan, int FilesPicked, string? FolderUsed, string? Reason);

    /// <summary>
    /// Sentinel value: pass <see cref="TimeSpan.MaxValue"/> as the window to scan
    /// the entire folder, ignoring file age completely. Used for the "Import all"
    /// menu entry, which lets the user backfill screenshots taken before they
    /// configured the folder (or older than the default rolling window).
    /// </summary>
    public static readonly TimeSpan WindowAll = TimeSpan.MaxValue;

    /// <summary>
    /// Forces a one-shot scan of the configured folder, picking up files modified
    /// during the last <paramref name="window"/>. Useful from the UI to recover
    /// from a missed FileSystemWatcher event without restarting the app.
    /// Pass <see cref="WindowAll"/> to disable the age cutoff entirely.
    /// </summary>
    public async Task<RescanResult> RescanAsync(TimeSpan? window = null, CancellationToken ct = default)
    {
        // Defensively re-resolve from the SettingsViewModel — the watcher's
        // OnSettingsChanged hook only fires for property changes, not for the
        // initial value. If the user opened the app for the first time without
        // a configured folder and only set it later via Settings, _currentFolder
        // might be stale.
        var folder = _currentFolder;
        if (string.IsNullOrWhiteSpace(folder))
        {
            folder = _settings.ScreenshotsFolder;
            if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
            {
                _logger.LogInformation("Rescan: lazy-reconfiguring on {Folder} (watcher had no folder).", folder);
                Reconfigure(folder);
                folder = _currentFolder;
            }
        }

        if (string.IsNullOrWhiteSpace(folder))
        {
            return new RescanResult(false, 0, null,
                "No screenshots folder is configured. Open Settings → Screenshots folder, pick or paste your Star Citizen screenshots folder, then try again.");
        }
        if (!Directory.Exists(folder))
        {
            return new RescanResult(false, 0, folder,
                $"The configured folder does not exist:\n{folder}\n\nFix the path in Settings → Screenshots folder.");
        }

        var effectiveWindow = window ?? InitialScanWindow;
        var picked = await CountAndScanAsync(folder, effectiveWindow, ct).ConfigureAwait(false);
        var windowLabel = FormatWindow(effectiveWindow);
        return new RescanResult(true, picked, folder,
            picked == 0
                ? $"Folder is reachable but no screenshots {windowLabel} were found."
                : $"Picked up {picked} screenshot(s) {windowLabel}.");
    }

    private static string FormatWindow(TimeSpan w)
    {
        if (w == WindowAll) return "in the folder";
        if (w.TotalMinutes < 60) return $"from the last {(int)w.TotalMinutes} min";
        if (w.TotalHours < 24) return $"from the last {(int)w.TotalHours} h";
        return $"from the last {(int)w.TotalDays} day(s)";
    }

    private async Task<int> CountAndScanAsync(string folder, TimeSpan window, CancellationToken ct)
    {
        try
        {
            var alreadySent = await GetAlreadySentNamesAsync(ct).ConfigureAwait(false);

            // WindowAll bypasses the cutoff entirely (used by the "Import all" menu).
            var enumerable = new DirectoryInfo(folder)
                .EnumerateFiles()
                .Where(f => IsAllowed(f.FullName));

            if (window != WindowAll)
            {
                var cutoff = DateTime.UtcNow - window;
                enumerable = enumerable.Where(f => f.LastWriteTimeUtc >= cutoff);
            }

            // Always skip files that match a SUCCESSFUL production submission.
            // The user explicitly asked for this behaviour: re-OCRing a file
            // they already shipped is wasted work and pollutes the inbox.
            var allInWindow = enumerable.OrderBy(f => f.LastWriteTimeUtc).ToList();
            var files = allInWindow.Where(f => !alreadySent.Contains(f.Name)).ToList();
            var skipped = allInWindow.Count - files.Count;
            if (skipped > 0)
            {
                _logger.LogInformation("Rescan: skipping {Skipped} file(s) already submitted to UEX.", skipped);
            }

            foreach (var f in files)
            {
                ct.ThrowIfCancellationRequested();
                await EnqueueWhenStableAsync(f.FullName, ct).ConfigureAwait(false);
            }
            return files.Count;
        }
        catch (OperationCanceledException) { return 0; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Rescan failed for {Folder}", folder);
            return 0;
        }
    }

    /// <summary>Currently watched folder, or null if none.</summary>
    public string? CurrentFolder => _currentFolder;

    private void OnFile(object? sender, FileSystemEventArgs e)
    {
        if (!IsAllowed(e.FullPath)) return;
        var ct = _stopCts?.Token ?? CancellationToken.None;
        _ = Task.Run(async () => await EnqueueWhenStableAsync(e.FullPath, ct).ConfigureAwait(false), ct);
    }

    private async Task EnqueueWhenStableAsync(string path, CancellationToken ct)
    {
        try
        {
            if (!await WaitForStableSizeAsync(path, ct).ConfigureAwait(false))
            {
                _logger.LogWarning("File never stabilised, skipping: {Path}", path);
                return;
            }
            _logger.LogInformation("Auto-import from watched folder: {Path}", path);
            _coordinator.EnqueueAndProcess(path);
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to handle new file: {Path}", path);
        }
    }

    /// <summary>
    /// Polls the file size until two consecutive samples agree within the StableSizeWindow.
    /// Avoids reading the file while the OS / game is still flushing it to disk.
    /// </summary>
    private static async Task<bool> WaitForStableSizeAsync(string path, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + StableSizeTimeout;
        long? lastSize = null;
        DateTimeOffset? lastSeen = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists) return false;

                if (lastSize == fi.Length
                    && lastSeen is { } at
                    && (DateTimeOffset.UtcNow - at) >= StableSizeWindow)
                {
                    return true;
                }

                if (lastSize != fi.Length)
                {
                    lastSize = fi.Length;
                    lastSeen = DateTimeOffset.UtcNow;
                }
            }
            catch (IOException)
            {
                // file still locked by writer
            }

            await Task.Delay(StableSizePollInterval, ct).ConfigureAwait(false);
        }
        return false;
    }

    private static bool IsAllowed(string path)
    {
        var ext = Path.GetExtension(path);
        return AllowedExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase);
    }

    private void DisposeWatcher()
    {
        if (_fsw is null) return;
        _fsw.EnableRaisingEvents = false;
        _fsw.Dispose();
        _fsw = null;
        _currentFolder = null;
    }

    public void Dispose() => DisposeWatcher();
}
