using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DataRunner.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace DataRunner.App.ViewModels;

public sealed partial class DiagnosticsViewModel : ObservableObject
{
    private readonly ISubmissionHistory _history;
    private readonly IAppPreferences _prefs;
    private readonly ICatalogProvider _catalog;
    private readonly ILogger<DiagnosticsViewModel> _logger;

    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SC-DataRunnerNet");

    private static readonly string LogsDir = Path.Combine(DataDir, "logs");
    private static readonly string DbPath = Path.Combine(DataDir, "history.sqlite");
    private static readonly string PrefsPath = Path.Combine(DataDir, "prefs.json");

    // --- System info ---
    [ObservableProperty] private string _appVersion = "";
    [ObservableProperty] private string _runtimeInfo = "";
    [ObservableProperty] private string _dataFolderPath = DataDir;
    [ObservableProperty] private string _logsFolderPath = LogsDir;
    [ObservableProperty] private string _dbFilePath = DbPath;
    [ObservableProperty] private string _dbSizeLabel = "";
    [ObservableProperty] private string _catalogStatus = "";

    // --- Logs ---
    [ObservableProperty] private string _logContent = "";
    [ObservableProperty] private bool _isLoadingLogs;
    [ObservableProperty] private int _logLinesToShow = 200;

    // --- Submission detail ---
    public ObservableCollection<SubmissionRecord> RecentSubmissions { get; } = new();
    [ObservableProperty] private SubmissionRecord? _selectedSubmission;
    [ObservableProperty] private string _selectedRequestJson = "";
    [ObservableProperty] private string _selectedResponseJson = "";

    // --- Feedback ---
    [ObservableProperty] private string _statusMessage = "";

    public DiagnosticsViewModel(
        ISubmissionHistory history,
        IAppPreferences prefs,
        ICatalogProvider catalog,
        ILogger<DiagnosticsViewModel> logger)
    {
        _history = history;
        _prefs = prefs;
        _catalog = catalog;
        _logger = logger;

        _ = InitializeAsync();
    }

    partial void OnSelectedSubmissionChanged(SubmissionRecord? value)
    {
        if (value is null)
        {
            SelectedRequestJson = "";
            SelectedResponseJson = "";
            return;
        }

        SelectedRequestJson = TryFormatJson(value.RequestJson);
        SelectedResponseJson = TryFormatJson(value.ResponseJson);
    }

    private async Task InitializeAsync()
    {
        LoadSystemInfo();
        await Task.WhenAll(
            LoadLogsAsync(),
            LoadRecentSubmissionsAsync());
    }

    private void LoadSystemInfo()
    {
        AppVersion = App.GetAppVersion();
        RuntimeInfo = $".NET {Environment.Version} / {Environment.OSVersion} / {(Environment.Is64BitProcess ? "x64" : "x86")}";

        if (File.Exists(DbPath))
        {
            var fi = new FileInfo(DbPath);
            DbSizeLabel = fi.Length switch
            {
                < 1024 => $"{fi.Length} B",
                < 1024 * 1024 => $"{fi.Length / 1024.0:F1} KB",
                _ => $"{fi.Length / (1024.0 * 1024.0):F2} MB",
            };
        }
        else
        {
            DbSizeLabel = "not found";
        }

        CatalogStatus = _catalog.LastRefreshedAt is { } at
            ? $"{_catalog.Commodities.Count} commodities, {_catalog.CommodityTerminals.Count} terminals (refreshed {at.LocalDateTime:g})"
            : "not loaded";
    }

    [RelayCommand]
    private async Task LoadLogsAsync()
    {
        IsLoadingLogs = true;
        try
        {
            var today = DateTime.Now.ToString("yyyyMMdd");
            var logFile = Path.Combine(LogsDir, $"app-{today}.log");

            if (!File.Exists(logFile))
            {
                var candidates = Directory.Exists(LogsDir)
                    ? Directory.GetFiles(LogsDir, "app-*.log")
                        .OrderByDescending(f => f)
                        .FirstOrDefault()
                    : null;

                logFile = candidates;
            }

            if (logFile is null || !File.Exists(logFile))
            {
                LogContent = "No log files found.";
                return;
            }

            // Read with sharing so Serilog can keep writing
            await using var fs = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            var allText = await sr.ReadToEndAsync();

            var lines = allText.Split('\n');
            if (lines.Length > LogLinesToShow)
            {
                var tail = lines[^LogLinesToShow..];
                LogContent = $"[… showing last {LogLinesToShow} of {lines.Length} lines from {Path.GetFileName(logFile)}]\n"
                           + string.Join('\n', tail);
            }
            else
            {
                LogContent = $"[{Path.GetFileName(logFile)} — {lines.Length} lines]\n" + allText;
            }
        }
        catch (Exception ex)
        {
            LogContent = $"Failed to read logs: {ex.Message}";
            _logger.LogWarning(ex, "Diagnostics: failed to read log file.");
        }
        finally
        {
            IsLoadingLogs = false;
        }
    }

    [RelayCommand]
    private async Task LoadRecentSubmissionsAsync()
    {
        try
        {
            var rows = await _history.GetAllAsync(limit: 50);
            RecentSubmissions.Clear();
            foreach (var r in rows) RecentSubmissions.Add(r);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Diagnostics: failed to load submissions.");
        }
    }

    [RelayCommand]
    private void OpenLogsFolder()
    {
        if (Directory.Exists(LogsDir))
            Process.Start(new ProcessStartInfo { FileName = LogsDir, UseShellExecute = true });
    }

    [RelayCommand]
    private void OpenDataFolder()
    {
        if (Directory.Exists(DataDir))
            Process.Start(new ProcessStartInfo { FileName = DataDir, UseShellExecute = true });
    }

    [RelayCommand]
    private async Task CopyBugReportAsync()
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== SC DataRunner Bug Report ===");
            sb.AppendLine($"Version: {AppVersion}");
            sb.AppendLine($"Runtime: {RuntimeInfo}");
            sb.AppendLine($"Data dir: {DataDir}");
            sb.AppendLine($"DB size: {DbSizeLabel}");
            sb.AppendLine($"Catalog: {CatalogStatus}");
            sb.AppendLine();

            // Preferences (non-secret)
            sb.AppendLine("--- Preferences ---");
            sb.AppendLine($"AttachScreenshot: {_prefs.AttachScreenshotOnSubmit}");
            sb.AppendLine($"DeleteAfterSubmit: {_prefs.DeleteScreenshotAfterSubmit}");
            sb.AppendLine($"DefaultProduction: {_prefs.DefaultIsProduction}");
            sb.AppendLine($"ScreenshotsFolder: {_prefs.ScreenshotsFolder ?? "<not set>"}");
            sb.AppendLine();

            // Last 5 submissions summary
            sb.AppendLine("--- Last 5 submissions ---");
            var recent = await _history.GetAllAsync(limit: 5);
            foreach (var r in recent)
            {
                sb.AppendLine($"  [{r.At.LocalDateTime:yyyy-MM-dd HH:mm:ss}] " +
                              $"Terminal={r.TerminalDisplayName ?? r.IdTerminal.ToString()} " +
                              $"OK={r.Ok} HTTP={r.HttpStatusCode} " +
                              $"API={r.ApiStatus} Msg={r.ApiMessage}");
            }
            sb.AppendLine();

            // Last 50 log lines
            sb.AppendLine("--- Recent logs (tail) ---");
            var today = DateTime.Now.ToString("yyyyMMdd");
            var logFile = Path.Combine(LogsDir, $"app-{today}.log");
            if (!File.Exists(logFile))
            {
                logFile = Directory.Exists(LogsDir)
                    ? Directory.GetFiles(LogsDir, "app-*.log")
                        .OrderByDescending(f => f)
                        .FirstOrDefault()
                    : null;
            }

            if (logFile is not null && File.Exists(logFile))
            {
                await using var fs = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sr = new StreamReader(fs);
                var text = await sr.ReadToEndAsync();
                var lines = text.Split('\n');
                var tail = lines.Length > 50 ? lines[^50..] : lines;
                foreach (var l in tail) sb.AppendLine(l);
            }
            else
            {
                sb.AppendLine("  (no log file found)");
            }

            System.Windows.Clipboard.SetText(sb.ToString());
            StatusMessage = "Bug report copied to clipboard!";

            _ = ClearStatusAfterDelay();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Copy failed: {ex.Message}";
            _logger.LogWarning(ex, "Diagnostics: failed to copy bug report.");
        }
    }

    [RelayCommand]
    private void CopySubmissionJson()
    {
        if (SelectedSubmission is null) return;
        var sb = new StringBuilder();
        sb.AppendLine("=== Request JSON ===");
        sb.AppendLine(TryFormatJson(SelectedSubmission.RequestJson));
        sb.AppendLine();
        sb.AppendLine("=== Response JSON ===");
        sb.AppendLine(TryFormatJson(SelectedSubmission.ResponseJson));

        System.Windows.Clipboard.SetText(sb.ToString());
        StatusMessage = "JSON copied to clipboard!";
        _ = ClearStatusAfterDelay();
    }

    private async Task ClearStatusAfterDelay()
    {
        await Task.Delay(3000);
        StatusMessage = "";
    }

    private static string TryFormatJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return "(empty)";
        try
        {
            using var doc = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            return json;
        }
    }
}
