namespace DataRunner.Core.Abstractions;

/// <summary>
/// User-tunable, NON-secret application preferences. Persisted to a small
/// JSON file under %LOCALAPPDATA%\SC-DataRunnerNet\prefs.json.
/// Secrets (UEX key) live in <see cref="ISecretKeyStore"/>, not here.
/// </summary>
public interface IAppPreferences
{
    /// <summary>
    /// When true, the source screenshot is base64-encoded and attached to every
    /// /data_submit POST as the `screenshot` field.
    ///
    /// REQUIRED for new datarunners (UEX enforces a 90-day evaluation period
    /// during which submissions without a screenshot are rejected with
    /// `not_allowed` / `screenshot_required`). Veteran datarunners may turn
    /// this off after their evaluation period, for privacy / bandwidth reasons.
    ///
    /// Default: true.
    /// </summary>
    bool AttachScreenshotOnSubmit { get; set; }

    /// <summary>
    /// LEGACY single-folder slot kept for backward compatibility with prefs
    /// files written before LIVE/PTU were split into two slots. Reading this
    /// property always returns <see cref="LiveScreenshotsFolder"/>; setting
    /// it forwards the value to <see cref="LiveScreenshotsFolder"/> too.
    ///
    /// New code should use <see cref="LiveScreenshotsFolder"/> /
    /// <see cref="PtuScreenshotsFolder"/> directly.
    /// </summary>
    string? ScreenshotsFolder { get; set; }

    /// <summary>
    /// Folder Star Citizen writes LIVE-channel screenshots to. Files dropped
    /// here are tagged with <see cref="DataRunner.Core.Models.GameBranch.Live"/>
    /// and submitted to UEX with the current LIVE build number resolved from
    /// /game_versions.
    /// </summary>
    string? LiveScreenshotsFolder { get; set; }

    /// <summary>
    /// Folder Star Citizen writes PTU-channel screenshots to. Optional — when
    /// empty / not set, the watcher only monitors the LIVE slot. Files dropped
    /// here are tagged with <see cref="DataRunner.Core.Models.GameBranch.Ptu"/>
    /// and submitted with the current PTU build number from /game_versions.
    /// </summary>
    string? PtuScreenshotsFolder { get; set; }

    /// <summary>
    /// When true, the source .png file is deleted from disk immediately after a
    /// successful submission to UEX. Skips deletion on test/failed submissions
    /// so users can retry. The submission history (SQLite) keeps a record of
    /// the file name + payload regardless, so nothing is lost from the audit log.
    ///
    /// Default: true (user preference, configurable in Settings).
    /// </summary>
    bool DeleteScreenshotAfterSubmit { get; set; }

    /// <summary>
    /// Default value for the `is_production` flag of every new submission.
    /// When true, submissions go LIVE on UEX (prices update for everyone).
    /// When false, UEX treats them as TEST (recorded but not live).
    ///
    /// Centralized here so the user picks their mode ONCE in Settings instead
    /// of being asked at every screenshot — which is both noisy and dangerous
    /// (a hurried user can flip it on without thinking).
    ///
    /// Default: true. The whole point of running this tool IS contributing
    /// live price data to UEX; users who specifically want to test their
    /// setup can flip it off in Settings.
    /// </summary>
    bool DefaultIsProduction { get; set; }

    /// <summary>
    /// When true, the inbox column is collapsed to a thin strip with just a
    /// re-expand button. Frees ~280px of horizontal space for the editor and
    /// the optional side-by-side screenshot panel. Persisted so the user's
    /// preferred layout survives app restarts.
    ///
    /// Default: false (inbox visible).
    /// </summary>
    bool InboxCollapsed { get; set; }

    /// <summary>
    /// When true, the main navigation sidebar (Inbox / Targets / …) is shrunk
    /// to a narrow strip with icon-only buttons, freeing horizontal space for
    /// the main content. Same idea as <see cref="InboxCollapsed"/> but for the
    /// left app chrome.
    ///
    /// Default: false (labels visible).
    /// </summary>
    bool SidebarCollapsed { get; set; }

    /// <summary>
    /// When true, the editor renders the source screenshot in a panel docked
    /// to the right of the validation form, with a draggable splitter between
    /// them. Lets the user verify OCR output against the source image without
    /// alt-tabbing or opening the floating viewer. Auto-disables itself if
    /// the editor area is too narrow to fit both panes (see EditorMinWidth in
    /// ScreenshotEditView).
    ///
    /// Default: false (the user opts in via the title-bar toggle).
    /// </summary>
    bool SideBySideScreenshot { get; set; }

    /// <summary>
    /// UEX vehicle id last picked in the Trade Routes view. Restored on the
    /// next session so the user doesn't have to re-pick their ship. A null
    /// here means "no vehicle filter" — the routes view shows every container
    /// size. Note: routes-related origin/destination/scope/investment values
    /// are NOT mirrored here because they already round-trip through
    /// <c>trade_routes_cache.json</c> as part of <c>TradeRouteQuery</c>;
    /// vehicle is purely a client-side filter that the cache doesn't carry.
    ///
    /// Default: null.
    /// </summary>
    int? RoutesSelectedVehicleId { get; set; }

    /// <summary>
    /// Last position of the Trader↔Datarunner blend slider in the Trade
    /// Routes view (0 = pure trader profit, 100 = pure datarunner refresh
    /// value). Persisted because rebuilding the routes list with a new
    /// score weighting is a non-trivial recompute and resetting it on every
    /// app start is annoying for users who consistently work in datarunner
    /// mode.
    ///
    /// Default: 30 (trader-leaning blend that surfaces both axes).
    /// </summary>
    double RoutesDatarunnerSliderValue { get; set; }

    // -----------------------------------------------------------------------
    // TRADE ROUTES — toggle filters, mirror of the UEX website pill bar.
    //
    // All flags default to false (no constraint = show everything UEX returned).
    // When a flag is true, the corresponding constraint is ANDed into the
    // client-side filter predicate. Multiple flags combine: e.g. "Legal" +
    // "Ground" = only legal commodities AND ground-to-ground routes.
    //
    // Semantics for "endpoint" flags (Loading / AutoLoad / Monitored / Space /
    // Ground / Refuel) — both endpoints must satisfy the criterion. This matches
    // typical hauler intent ("I want a route I can refuel at on both ends",
    // "I want ground-to-ground only") and keeps results predictable.
    //
    // "Legal" applies to the route's commodity (looked up via ICatalogProvider).
    // "Predicted" surfaces routes where at least one side has zero user-confirmed
    // price reports (= predicted price), useful for datarunners targeting
    // refresh runs.
    //
    // Persisted so a user who always trades legal/ground-only doesn't have to
    // re-toggle the filters on every app launch.
    // -----------------------------------------------------------------------

    /// <summary>Routes filter — keep only routes whose ORIGIN AND DESTINATION both have a loading dock.</summary>
    bool RoutesFilterLoadingDock { get; set; }

    /// <summary>Routes filter — keep only routes where both endpoints expose a freight elevator (auto-load).</summary>
    bool RoutesFilterFreightElevator { get; set; }

    /// <summary>Routes filter — keep only routes whose commodity is NOT flagged illegal.</summary>
    bool RoutesFilterLegal { get; set; }

    /// <summary>Routes filter — keep only routes where both endpoints are monitored terminals.</summary>
    bool RoutesFilterMonitored { get; set; }

    /// <summary>Routes filter — keep only routes between two space stations.</summary>
    bool RoutesFilterSpace { get; set; }

    /// <summary>Routes filter — keep only routes between two ground-based terminals.</summary>
    bool RoutesFilterGround { get; set; }

    /// <summary>Routes filter — keep only routes where both endpoints offer refuelling.</summary>
    bool RoutesFilterRefuel { get; set; }

    /// <summary>Routes filter — keep only routes where at least one side has a predicted (no user-confirmed) price.</summary>
    bool RoutesFilterPredicted { get; set; }

    /// <summary>
    /// Routes filter — minimum effective profit (aUEC) the route must yield
    /// for the user's current budget. Null = no minimum. Lets the user prune
    /// micro-runs (the budget cap inevitably surfaces routes worth a few
    /// thousand aUEC, which is rarely worth a quantum jump).
    /// </summary>
    long? RoutesMinProfit { get; set; }

    /// <summary>
    /// Routes filter — minimum profit per minute of quantum travel
    /// (aUEC/min). Null = no minimum. Surfaces routes that are actually
    /// efficient under realistic ship logistics; a 200K profit run that
    /// takes 10 minutes is worse than a 80K run that takes 2.
    /// </summary>
    long? RoutesMinProfitPerMinute { get; set; }

    /// <summary>
    /// Bindable name of the column that should be used as the default sort
    /// when the Trade Routes view is loaded. <c>null</c> = use the built-in
    /// "DatarunnerScore" descending fallback. Persisted across sessions so
    /// the user's preferred ranking sticks (e.g. "always sort by Profit
    /// descending").
    /// </summary>
    string? RoutesDefaultSortMember { get; set; }

    /// <summary>
    /// Sort direction applied to <see cref="RoutesDefaultSortMember"/>.
    /// Stored as the integer enum value of <c>System.ComponentModel.ListSortDirection</c>
    /// to avoid leaking a WPF/PresentationFramework dependency into the
    /// abstraction layer (Ascending=0, Descending=1).
    /// </summary>
    int RoutesDefaultSortDirection { get; set; }

    /// <summary>
    /// Delay (in milliseconds) inserted between two consecutive POSTs of the
    /// same batch send. Helps respect UEX's documented rate limits without
    /// hammering the API:
    ///   - hard cap: 1000 reports per 30 minutes
    ///   - per-(terminal, commodity) duplicated_report rule: 5 minutes
    ///
    /// 0 disables the throttle (one POST is fired right after the previous
    /// response lands). The smart-split planner already eliminates intra-batch
    /// duplicates so a small throttle is mostly there to avoid bursting the
    /// rate counter when sending dozens of distinct terminals back-to-back.
    ///
    /// Default: 1000 (one second between submissions).
    /// </summary>
    int BatchSubmissionDelayMs { get; set; }

    /// <summary>Persists the current state to disk.</summary>
    Task SaveAsync(CancellationToken ct = default);

    /// <summary>Reloads from disk (used at startup).</summary>
    Task LoadAsync(CancellationToken ct = default);
}
