using System.Text.Json;
using System.Text.Json.Serialization;
using DataRunner.Core.Abstractions;

namespace DataRunner.UexClient;

/// <summary>
/// JSON-backed implementation of <see cref="IAppPreferences"/>.
/// File lives at %LOCALAPPDATA%\SC-DataRunnerNet\prefs.json.
///
/// This file contains NO secrets — the UEX secret-key is stored separately
/// (DPAPI-encrypted) by <see cref="DpapiSecretKeyStore"/>.
/// </summary>
public sealed class JsonAppPreferences : IAppPreferences
{
    private readonly string _filePath;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public bool AttachScreenshotOnSubmit { get; set; } = true;
    public string? LiveScreenshotsFolder { get; set; }
    public string? PtuScreenshotsFolder { get; set; }

    /// <summary>
    /// Compatibility shim for callers that haven't been updated to the
    /// LIVE/PTU split yet. Reads/writes always go through
    /// <see cref="LiveScreenshotsFolder"/> — the LIVE channel is the
    /// historical default behaviour.
    /// </summary>
    public string? ScreenshotsFolder
    {
        get => LiveScreenshotsFolder;
        set => LiveScreenshotsFolder = value;
    }

    public bool DeleteScreenshotAfterSubmit { get; set; } = true;
    public bool DefaultIsProduction { get; set; } = true;
    public bool InboxCollapsed { get; set; } = false;
    public bool SideBySideScreenshot { get; set; } = false;

    public JsonAppPreferences(string? overridePath = null)
    {
        _filePath = overridePath ?? DefaultPath();
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
    }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_filePath)) return;
            await using var fs = File.OpenRead(_filePath);
            var dto = await JsonSerializer.DeserializeAsync<PrefsDto>(fs, JsonOpts, ct)
                .ConfigureAwait(false);
            if (dto is null) return;
            AttachScreenshotOnSubmit = dto.AttachScreenshotOnSubmit;

            // Migration: prior versions wrote a single `screenshotsFolder`
            // entry. We forward it to the LIVE slot when no LIVE/PTU values
            // are present yet. The legacy field is then dropped on the next
            // SaveAsync (it's no longer in PrefsDto's serialised output).
            LiveScreenshotsFolder = !string.IsNullOrWhiteSpace(dto.LiveScreenshotsFolder)
                ? dto.LiveScreenshotsFolder
                : dto.ScreenshotsFolder;
            PtuScreenshotsFolder = dto.PtuScreenshotsFolder;

            DeleteScreenshotAfterSubmit = dto.DeleteScreenshotAfterSubmit;
            DefaultIsProduction = dto.DefaultIsProduction;
            InboxCollapsed = dto.InboxCollapsed;
            SideBySideScreenshot = dto.SideBySideScreenshot;
        }
        catch
        {
            // Corrupted prefs file: keep in-memory defaults, will be overwritten
            // on next SaveAsync.
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var dto = new PrefsDto
            {
                AttachScreenshotOnSubmit = AttachScreenshotOnSubmit,
                LiveScreenshotsFolder = LiveScreenshotsFolder,
                PtuScreenshotsFolder = PtuScreenshotsFolder,
                DeleteScreenshotAfterSubmit = DeleteScreenshotAfterSubmit,
                DefaultIsProduction = DefaultIsProduction,
                InboxCollapsed = InboxCollapsed,
                SideBySideScreenshot = SideBySideScreenshot,
            };
            var json = JsonSerializer.Serialize(dto, JsonOpts);
            await File.WriteAllTextAsync(_filePath, json, ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    private static string DefaultPath()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SC-DataRunnerNet",
            "prefs.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed class PrefsDto
    {
        public bool AttachScreenshotOnSubmit { get; set; } = true;

        /// <summary>
        /// LIVE-channel screenshots folder. Successor of the legacy
        /// <see cref="ScreenshotsFolder"/> single-slot field.
        /// </summary>
        public string? LiveScreenshotsFolder { get; set; }

        /// <summary>PTU-channel screenshots folder. Optional.</summary>
        public string? PtuScreenshotsFolder { get; set; }

        /// <summary>
        /// Legacy single-folder slot. Kept for read-side migration only —
        /// when present, its value is forwarded to <see cref="LiveScreenshotsFolder"/>.
        /// We don't write it anymore so prefs.json on disk converges to the
        /// new shape after the next save.
        /// </summary>
        public string? ScreenshotsFolder { get; set; }

        public bool DeleteScreenshotAfterSubmit { get; set; } = true;
        public bool DefaultIsProduction { get; set; } = true;
        public bool InboxCollapsed { get; set; } = false;
        public bool SideBySideScreenshot { get; set; } = false;
    }
}
