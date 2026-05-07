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
    public bool SidebarCollapsed { get; set; } = false;
    public bool SideBySideScreenshot { get; set; } = false;
    public int? RoutesSelectedVehicleId { get; set; }
    public double RoutesDatarunnerSliderValue { get; set; } = 30.0;

    public bool RoutesFilterLoadingDock { get; set; }
    public bool RoutesFilterFreightElevator { get; set; }
    public bool RoutesFilterLegal { get; set; }
    public bool RoutesFilterMonitored { get; set; }
    public bool RoutesFilterSpace { get; set; }
    public bool RoutesFilterGround { get; set; }
    public bool RoutesFilterRefuel { get; set; }
    public bool RoutesFilterPredicted { get; set; }

    public long? RoutesMinProfit { get; set; }
    public long? RoutesMinProfitPerMinute { get; set; }

    public string? RoutesDefaultSortMember { get; set; }
    public int RoutesDefaultSortDirection { get; set; } = 1;

    public int BatchSubmissionDelayMs { get; set; } = 1000;

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
            SidebarCollapsed = dto.SidebarCollapsed;
            SideBySideScreenshot = dto.SideBySideScreenshot;
            RoutesSelectedVehicleId = dto.RoutesSelectedVehicleId;
            RoutesDatarunnerSliderValue = dto.RoutesDatarunnerSliderValue;
            RoutesFilterLoadingDock = dto.RoutesFilterLoadingDock;
            RoutesFilterFreightElevator = dto.RoutesFilterFreightElevator;
            RoutesFilterLegal = dto.RoutesFilterLegal;
            RoutesFilterMonitored = dto.RoutesFilterMonitored;
            RoutesFilterSpace = dto.RoutesFilterSpace;
            RoutesFilterGround = dto.RoutesFilterGround;
            RoutesFilterRefuel = dto.RoutesFilterRefuel;
            RoutesFilterPredicted = dto.RoutesFilterPredicted;
            RoutesMinProfit = dto.RoutesMinProfit;
            RoutesMinProfitPerMinute = dto.RoutesMinProfitPerMinute;
            RoutesDefaultSortMember = dto.RoutesDefaultSortMember;
            RoutesDefaultSortDirection = dto.RoutesDefaultSortDirection;
            // Treat 0 in the persisted file as "explicit user override" rather
            // than "missing", since 0 is a valid (no-throttle) value. Negative
            // values would be nonsense; clamp to the default to be defensive.
            BatchSubmissionDelayMs = dto.BatchSubmissionDelayMs >= 0 ? dto.BatchSubmissionDelayMs : 1000;
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
                SidebarCollapsed = SidebarCollapsed,
                SideBySideScreenshot = SideBySideScreenshot,
                RoutesSelectedVehicleId = RoutesSelectedVehicleId,
                RoutesDatarunnerSliderValue = RoutesDatarunnerSliderValue,
                RoutesFilterLoadingDock = RoutesFilterLoadingDock,
                RoutesFilterFreightElevator = RoutesFilterFreightElevator,
                RoutesFilterLegal = RoutesFilterLegal,
                RoutesFilterMonitored = RoutesFilterMonitored,
                RoutesFilterSpace = RoutesFilterSpace,
                RoutesFilterGround = RoutesFilterGround,
                RoutesFilterRefuel = RoutesFilterRefuel,
                RoutesFilterPredicted = RoutesFilterPredicted,
                RoutesMinProfit = RoutesMinProfit,
                RoutesMinProfitPerMinute = RoutesMinProfitPerMinute,
                RoutesDefaultSortMember = RoutesDefaultSortMember,
                RoutesDefaultSortDirection = RoutesDefaultSortDirection,
                BatchSubmissionDelayMs = BatchSubmissionDelayMs,
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
        public bool SidebarCollapsed { get; set; } = false;
        public bool SideBySideScreenshot { get; set; } = false;

        /// <summary>UEX vehicle id last picked in the Trade Routes view, or
        /// <c>null</c> for "no vehicle filter".</summary>
        public int? RoutesSelectedVehicleId { get; set; }

        /// <summary>Last Trader↔Datarunner slider position (0..100, default 30).</summary>
        public double RoutesDatarunnerSliderValue { get; set; } = 30.0;

        /// <summary>Trade Routes — pill toggle: keep only routes whose endpoints both have a loading dock.</summary>
        public bool RoutesFilterLoadingDock { get; set; }

        /// <summary>Trade Routes — pill toggle: keep only routes whose endpoints both expose a freight elevator.</summary>
        public bool RoutesFilterFreightElevator { get; set; }

        /// <summary>Trade Routes — pill toggle: hide routes trading illegal commodities.</summary>
        public bool RoutesFilterLegal { get; set; }

        /// <summary>Trade Routes — pill toggle: keep only routes whose endpoints are both monitored.</summary>
        public bool RoutesFilterMonitored { get; set; }

        /// <summary>Trade Routes — pill toggle: keep only space-station ↔ space-station routes.</summary>
        public bool RoutesFilterSpace { get; set; }

        /// <summary>Trade Routes — pill toggle: keep only ground ↔ ground routes.</summary>
        public bool RoutesFilterGround { get; set; }

        /// <summary>Trade Routes — pill toggle: keep only routes where both endpoints offer refuelling.</summary>
        public bool RoutesFilterRefuel { get; set; }

        /// <summary>Trade Routes — pill toggle: keep only routes with at least one predicted price (0 user reports on a side).</summary>
        public bool RoutesFilterPredicted { get; set; }

        /// <summary>Trade Routes — minimum effective profit (aUEC) for a route to be displayed. Null = no min.</summary>
        public long? RoutesMinProfit { get; set; }

        /// <summary>Trade Routes — minimum aUEC/min (efficiency) for a route to be displayed. Null = no min.</summary>
        public long? RoutesMinProfitPerMinute { get; set; }

        /// <summary>Trade Routes — favourited default sort column path (null = DatarunnerScore fallback).</summary>
        public string? RoutesDefaultSortMember { get; set; }

        /// <summary>
        /// Trade Routes — direction for <see cref="RoutesDefaultSortMember"/> (0 = asc, 1 = desc).
        /// Defaults to 1 (descending) which matches how every numeric column on this view is most useful.
        /// </summary>
        public int RoutesDefaultSortDirection { get; set; } = 1;

        /// <summary>
        /// Throttle (ms) between two POSTs of the same batch send. Default
        /// 1000 ms. 0 disables the throttle. See <see cref="IAppPreferences.BatchSubmissionDelayMs"/>.
        /// </summary>
        public int BatchSubmissionDelayMs { get; set; } = 1000;
    }
}
