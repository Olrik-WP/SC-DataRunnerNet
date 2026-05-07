using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DataRunner.Core.Abstractions;
using DataRunner.Core.Models;
using Microsoft.Extensions.Logging;

namespace DataRunner.App.ViewModels;

/// <summary>
/// View model for the "Trade routes" page.
///
/// Reads from <see cref="ITradeRouteProvider"/> (UEX <c>commodities_routes</c>
/// + datarunner stale-overlay) and exposes a filterable / sortable collection
/// to a <see cref="DataGrid"/>. Vehicle selection constrains visible routes
/// by container size; the <c>Trader ↔ Datarunner</c> slider re-weights the
/// composite score used for sorting in place.
///
/// API hygiene: this VM does NOT trigger an API call on construction. It loads
/// from the provider's disk cache. <see cref="EnsureLoadedAsync"/> is called
/// when the user navigates to the page; it triggers a non-forced refresh that
/// the provider will skip if cache is still within TTL.
/// </summary>
public sealed partial class RoutesViewModel : ObservableObject
{
    private static readonly char[] TerminalQueryTokenSeparators =
        { ' ', '\t', '·', '/', '\\', '|', '-', '–', '—', ',', '(', ')', '[', ']' };

    private readonly ITradeRouteProvider _provider;
    private readonly IVehicleCatalog _vehicles;
    private readonly ICatalogProvider _catalog;
    private readonly IAppPreferences _prefs;
    private readonly ILogger<RoutesViewModel> _logger;
    private readonly Dispatcher _uiDispatcher;

    /// <summary>Backing collection bound to the DataGrid (via <see cref="View"/>).</summary>
    public ObservableCollection<TradeRouteProposal> AllProposals { get; } = new();

    /// <summary>Filtered + sorted view exposed to the UI.</summary>
    public ICollectionView View { get; }

    /// <summary>Cargo-capable vehicles for the combo, sorted by SCU desc.</summary>
    public ObservableCollection<UexVehicle> Vehicles { get; } = new();

    /// <summary>Live autocomplete suggestions for the origin terminal search box.</summary>
    public ObservableCollection<UexTerminal> OriginSuggestions { get; } = new();

    /// <summary>Live autocomplete suggestions for the optional destination terminal.</summary>
    public ObservableCollection<UexTerminal> DestinationSuggestions { get; } = new();

    [ObservableProperty] private bool _isRefreshing;
    [ObservableProperty] private string _statusMessage = "Pick an origin terminal to fetch trade routes.";
    [ObservableProperty] private DateTimeOffset? _lastRefreshedAt;

    /// <summary>Free-text search bound to the origin terminal box (drives <see cref="OriginSuggestions"/>).</summary>
    [ObservableProperty] private string? _originSearch;
    [ObservableProperty] private UexTerminal? _selectedOrigin;

    [ObservableProperty] private string? _destinationSearch;
    [ObservableProperty] private UexTerminal? _selectedDestination;

    [ObservableProperty] private UexVehicle? _selectedVehicle;

    /// <summary>Optional UEC budget cap. Null = no cap.</summary>
    [ObservableProperty] private long? _investmentMaxUec;

    /// <summary>
    /// Geographic scope for the origin filter. <see cref="RouteScope.Orbit"/> is
    /// the default — picking "Admin - CRU-L1" then aggregates routes from every
    /// sibling terminal at CRU-L1, matching what users see on the UEX website.
    /// Switch to <see cref="RouteScope.Terminal"/> to drill into one specific shop.
    /// </summary>
    [ObservableProperty] private RouteScope _originScope = RouteScope.Orbit;

    [ObservableProperty] private RouteScope _destinationScope = RouteScope.Terminal;

    /// <summary>Human-readable label of the resolved origin scope (e.g. "CRU-L1", "Crusader").</summary>
    [ObservableProperty] private string? _originScopeLabel;

    /// <summary>Slider value 0 (pure trader profit) … 100 (pure datarunner stale-refresh).
    /// Default 30 — overridden by the persisted user preference at construction.</summary>
    [ObservableProperty] private double _datarunnerSliderValue = 30.0;

    // ---- Pill toggles (mirror of the UEX trade-routes filter bar) -------
    // All default to false (no constraint). Hydrated from prefs at startup
    // and persisted on every flip via <see cref="OnRoutesFilterChanged"/>.
    // They never trigger an API call — purely client-side <see cref="FilterPredicate"/>
    // tweaks on the cached proposals.

    /// <summary>Filter — keep only routes whose origin AND destination both have a loading dock.</summary>
    [ObservableProperty] private bool _filterLoadingDock;

    /// <summary>Filter — keep only routes where both endpoints expose a freight elevator (auto-load).</summary>
    [ObservableProperty] private bool _filterFreightElevator;

    /// <summary>Filter — hide routes trading illegal commodities (commodity catalog lookup).</summary>
    [ObservableProperty] private bool _filterLegal;

    /// <summary>Filter — keep only routes whose endpoints are both monitored terminals.</summary>
    [ObservableProperty] private bool _filterMonitored;

    /// <summary>Filter — keep only space-station ↔ space-station routes.</summary>
    [ObservableProperty] private bool _filterSpace;

    /// <summary>Filter — keep only ground ↔ ground routes.</summary>
    [ObservableProperty] private bool _filterGround;

    /// <summary>Filter — keep only routes where both endpoints offer refuelling.</summary>
    [ObservableProperty] private bool _filterRefuel;

    /// <summary>Filter — keep only routes where at least one side has a predicted (no user-confirmed) price.</summary>
    [ObservableProperty] private bool _filterPredicted;

    /// <summary>Filter — minimum effective profit (aUEC). Null = no minimum.</summary>
    [ObservableProperty] private long? _minProfit;

    /// <summary>Filter — minimum aUEC per minute of quantum travel. Null = no minimum.</summary>
    [ObservableProperty] private long? _minProfitPerMinute;

    /// <summary>
    /// Bindable name of the column whose ★ button is currently lit (= the
    /// favourited "default sort" target). Null = the built-in DatarunnerScore
    /// descending fallback. Comparing this against each column's
    /// <c>SortMemberPath</c> lets the header template light up the right
    /// star without wiring a multi-converter per column.
    /// </summary>
    [ObservableProperty] private string? _defaultSortMember;

    /// <summary>
    /// Direction for <see cref="DefaultSortMember"/>. Defaults to descending
    /// because every numeric/scoring column on this view (Profit, ROI, SCU,
    /// ETA, Score, Stale) is more useful sorted big-to-small. The user can
    /// click the column header to flip it; we capture the new direction in
    /// <see cref="OnDataGridSorting"/>.
    /// </summary>
    [ObservableProperty] private ListSortDirection _defaultSortDirection = ListSortDirection.Descending;

    /// <summary>While true, persistence-side handlers MUST NOT save. Used while
    /// hydrating from prefs to avoid writing back the value we just loaded.</summary>
    private bool _suppressPersist;

    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private int _filteredCount;

    /// <summary>
    /// While true, property-changed handlers MUST NOT trigger an API refresh.
    /// Used during boot reconciliation (the cached query gets multi-stage
    /// reflected into the UI: origin → destination → investment) to avoid
    /// 3 API calls in a row for the same effective query.
    /// </summary>
    private bool _suppressAutoRefresh;

    public RoutesViewModel(
        ITradeRouteProvider provider,
        IVehicleCatalog vehicles,
        ICatalogProvider catalog,
        IAppPreferences prefs,
        ILogger<RoutesViewModel> logger)
    {
        _provider = provider;
        _vehicles = vehicles;
        _catalog = catalog;
        _prefs = prefs;
        _logger = logger;
        _uiDispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

        View = CollectionViewSource.GetDefaultView(AllProposals);
        View.Filter = FilterPredicate;
        // Initial sort = the built-in DatarunnerScore fallback. If the user
        // had favourited a different column in a previous session,
        // <see cref="HydrateFromPreferences"/> will overwrite this below.
        View.SortDescriptions.Add(new SortDescription(nameof(TradeRouteProposal.DatarunnerScore), ListSortDirection.Descending));

        _provider.Refreshed += OnProviderRefreshedFromAnyThread;
        _vehicles.Refreshed += OnVehiclesRefreshedFromAnyThread;
        _catalog.Refreshed += OnCatalogRefreshedFromAnyThread;

        ReloadVehicles();
        ReloadFromProvider();
        ReflectProviderQueryIntoUi();
        HydrateFromPreferences();
        // Pre-populate the suggestion buckets so the dropdowns are not empty on
        // first open, even before the user types anything.
        UpdateOriginSuggestions();
        UpdateDestinationSuggestions();
    }

    /// <summary>
    /// Restore the bits of UI state that don't round-trip through the
    /// <c>TradeRouteQuery</c> cache (vehicle pick, slider position) from the
    /// persisted user preferences. Must run AFTER
    /// <see cref="ReloadVehicles"/> so the saved vehicle id can be matched
    /// against the catalog. Wraps the assignments in a guard flag so the
    /// regular OnXxxChanged handlers don't trigger a save-back of the value
    /// we just loaded (no-op in practice but keeps the disk file pristine).
    /// </summary>
    private void HydrateFromPreferences()
    {
        _suppressPersist = true;
        try
        {
            // Vehicle: match by id when the saved id is still in the
            // catalog. If the user switched UEX game version and the
            // vehicle was decommissioned, fall back to "no vehicle filter"
            // rather than poisoning the persisted state with a stale id.
            if (_prefs.RoutesSelectedVehicleId is { } vehicleId)
            {
                SelectedVehicle = Vehicles.FirstOrDefault(v => v.Id == vehicleId);
            }

            // Slider: clamp to the [0, 100] range in case prefs.json was
            // hand-edited or migrated from an older schema with a
            // different scale.
            DatarunnerSliderValue = Math.Clamp(_prefs.RoutesDatarunnerSliderValue, 0.0, 100.0);

            // Pill filter toggles. Mirroring the prefs back into the VM
            // here means the on-disk state IS the source of truth — flipping
            // a pill saves immediately, and re-opening the page restores
            // exactly what the user left enabled.
            FilterLoadingDock = _prefs.RoutesFilterLoadingDock;
            FilterFreightElevator = _prefs.RoutesFilterFreightElevator;
            FilterLegal = _prefs.RoutesFilterLegal;
            FilterMonitored = _prefs.RoutesFilterMonitored;
            FilterSpace = _prefs.RoutesFilterSpace;
            FilterGround = _prefs.RoutesFilterGround;
            FilterRefuel = _prefs.RoutesFilterRefuel;
            FilterPredicted = _prefs.RoutesFilterPredicted;
            MinProfit = _prefs.RoutesMinProfit;
            MinProfitPerMinute = _prefs.RoutesMinProfitPerMinute;

            // Default sort. Persisted enum direction is stored as int in
            // prefs (0 = ascending, 1 = descending) to keep the abstraction
            // free of any WPF dependency. Anything outside [0,1] is treated
            // as descending — the most useful ordering for every numeric
            // column on this view.
            DefaultSortMember = _prefs.RoutesDefaultSortMember;
            DefaultSortDirection = _prefs.RoutesDefaultSortDirection == 0
                ? ListSortDirection.Ascending
                : ListSortDirection.Descending;
            ApplyDefaultSort();
        }
        finally
        {
            _suppressPersist = false;
        }
    }

    /// <summary>
    /// Replaces <see cref="View"/>'s sort descriptions with whatever the
    /// user has favourited (or the DatarunnerScore fallback when nothing
    /// is favourited). Called at startup after hydration, and on every
    /// change to <see cref="DefaultSortMember"/> /
    /// <see cref="DefaultSortDirection"/> via the partial property hooks.
    /// </summary>
    private void ApplyDefaultSort()
    {
        var sortMember = !string.IsNullOrWhiteSpace(DefaultSortMember)
            ? DefaultSortMember!
            : nameof(TradeRouteProposal.DatarunnerScore);

        View.SortDescriptions.Clear();
        View.SortDescriptions.Add(new SortDescription(sortMember, DefaultSortDirection));
    }

    /// <summary>
    /// On startup the provider may already have a <see cref="ITradeRouteProvider.CurrentQuery"/>
    /// restored from disk cache. Mirror it into the VM's filter properties so the
    /// user sees what the displayed proposals were fetched for, instead of an
    /// empty filter bar with non-empty results.
    /// </summary>
    private void ReflectProviderQueryIntoUi()
    {
        if (_provider.CurrentQuery is not { } q) return;

        // Guard the multi-step reflection so the partial property changes
        // (origin set first, destination next, investment last) don't each
        // independently fire SetQueryAndRefreshAsync — the user's last
        // committed query is already cached, no API call is warranted here.
        _suppressAutoRefresh = true;
        try
        {
            // The cached query stores the resolved (Id, Scope) pair. We can't always
            // round-trip back to a specific UexTerminal when scope is Orbit/Planet
            // (a single ID maps to many terminals) — best effort: pick any terminal
            // from the catalog that shares the origin scope's ID, so the dropdown
            // shows at least one matching terminal that the user can re-pick.
            OriginScope = q.OriginScope;
            var originTerminal = ResolveTerminalForScope(q.OriginId, q.OriginScope);
            if (originTerminal is not null)
            {
                SelectedOrigin = originTerminal;
                OriginSearch = originTerminal.RichDisplayName;
            }

            if (q.DestinationId is { } destId)
            {
                DestinationScope = q.DestinationScope;
                var destTerminal = ResolveTerminalForScope(destId, q.DestinationScope);
                if (destTerminal is not null)
                {
                    SelectedDestination = destTerminal;
                    DestinationSearch = destTerminal.RichDisplayName;
                }
            }

            if (q.Investment is { } inv && inv > 0)
            {
                InvestmentMaxUec = inv;
            }

            if (originTerminal is not null)
            {
                OriginScopeLabel = ResolveScopeLabel(originTerminal, q.OriginScope);
            }
        }
        finally
        {
            _suppressAutoRefresh = false;
        }
    }

    private UexTerminal? ResolveTerminalForScope(int id, RouteScope scope) => scope switch
    {
        RouteScope.Terminal => _catalog.GetTerminal(id),
        RouteScope.Orbit => _catalog.CommodityTerminals.FirstOrDefault(t => t.IdOrbit == id),
        RouteScope.Planet => _catalog.CommodityTerminals.FirstOrDefault(t => t.IdPlanet == id),
        _ => null,
    };

    private void OnProviderRefreshedFromAnyThread(object? sender, EventArgs e)
    {
        if (_uiDispatcher.CheckAccess()) ReloadFromProvider();
        else _uiDispatcher.BeginInvoke((Action)ReloadFromProvider);
    }

    private void OnVehiclesRefreshedFromAnyThread(object? sender, EventArgs e)
    {
        if (_uiDispatcher.CheckAccess()) ReloadVehicles();
        else _uiDispatcher.BeginInvoke((Action)ReloadVehicles);
    }

    private void OnCatalogRefreshedFromAnyThread(object? sender, EventArgs e)
    {
        // Catalog refresh changes terminal display labels; rebuild current
        // suggestion lists so the user sees the latest names.
        if (_uiDispatcher.CheckAccess()) RecomputeSuggestionsFromCatalog();
        else _uiDispatcher.BeginInvoke((Action)RecomputeSuggestionsFromCatalog);
    }

    private void RecomputeSuggestionsFromCatalog()
    {
        UpdateOriginSuggestions();
        UpdateDestinationSuggestions();
    }

    /// <summary>
    /// Called by the View when the user navigates to the Trade Routes page.
    /// Triggers (a) vehicle catalog refresh if stale, (b) provider refresh if
    /// an origin is set. Both are non-forced and respect their own TTLs.
    /// </summary>
    public async Task EnsureLoadedAsync(CancellationToken ct = default)
    {
        try
        {
            await _vehicles.RefreshAsync(force: false, ct).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Vehicles catalog refresh failed; using cached snapshot.");
        }

        if (SelectedOrigin is null && _provider.CurrentQuery is null)
        {
            // Nothing to fetch yet: user must pick an origin first.
            return;
        }

        try
        {
            var hit = await _provider.RefreshAsync(force: false, ct).ConfigureAwait(true);
            if (hit) StatusMessage = $"Refreshed from UEX ({DateTimeOffset.Now:HH:mm})";
            else if (LastRefreshedAt is { } at)
                StatusMessage = $"Local cache from {at.ToLocalTime():yyyy-MM-dd HH:mm} (still fresh)";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Routes EnsureLoadedAsync failed");
            StatusMessage = "Could not refresh — showing local cache.";
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsRefreshing) return;
        if (SelectedOrigin is null)
        {
            StatusMessage = "Pick an origin terminal first.";
            return;
        }
        IsRefreshing = true;
        try
        {
            StatusMessage = "Calling UEX...";
            var query = BuildQuery();
            var hit = await _provider.SetQueryAndRefreshAsync(query, force: true).ConfigureAwait(true);
            if (hit)
            {
                StatusMessage = $"Refreshed from UEX ({DateTimeOffset.Now:HH:mm})";
            }
            else
            {
                var window = _provider.MinManualRefreshInterval;
                StatusMessage = $"Throttled. Manual refresh is limited to once every {(int)window.TotalSeconds}s.";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Manual trade-routes refresh failed");
            StatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    /// <summary>
    /// Pre-fills the origin from another part of the app (e.g. context menu in
    /// Targets view → "Open trade routes from this terminal in DataRunner").
    /// Switches focus to the matching catalog terminal and triggers a non-forced
    /// refresh so the user sees results immediately.
    /// </summary>
    public async Task PreFillFromTerminalAsync(int idTerminal, CancellationToken ct = default)
    {
        var terminal = _catalog.GetTerminal(idTerminal);
        if (terminal is null)
        {
            _logger.LogWarning("PreFillFromTerminalAsync: catalog has no terminal {Id}.", idTerminal);
            return;
        }

        SelectedOrigin = terminal;
        OriginSearch = terminal.RichDisplayName;

        // Reset destination/commodity filters: the user came from a Targets row,
        // they want to see the FULL spread of routes from that origin first.
        SelectedDestination = null;
        DestinationSearch = null;

        try
        {
            var query = BuildQuery();
            await _provider.SetQueryAndRefreshAsync(query, force: false, ct).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PreFillFromTerminalAsync: refresh failed for terminal {Id}", idTerminal);
        }
    }

    private TradeRouteQuery BuildQuery()
    {
        if (SelectedOrigin is null)
            throw new InvalidOperationException("Cannot build a route query without an origin terminal.");

        // Resolve the origin ID for the selected scope. We fall back gracefully
        // to a narrower scope when UEX hasn't classified the terminal at the
        // requested level (e.g. terminal has no IdOrbit set in the catalog).
        var (originId, originScope) = ResolveScopeId(SelectedOrigin, OriginScope);
        OriginScopeLabel = ResolveScopeLabel(SelectedOrigin, originScope);

        var (destinationId, destinationScope) = SelectedDestination is { } dest
            ? ResolveScopeId(dest, DestinationScope)
            : ((int?)null, RouteScope.Terminal);

        return new TradeRouteQuery(
            OriginId: originId,
            OriginScope: originScope,
            Investment: InvestmentMaxUec is { } i && i > 0 ? (int)Math.Min(int.MaxValue, i) : null,
            IdCommodity: null,
            DestinationId: destinationId,
            DestinationScope: destinationScope);
    }

    /// <summary>
    /// Resolves the appropriate UEX ID + scope for a terminal under the requested
    /// scope. Falls back to a narrower scope when the terminal isn't classified at
    /// the requested level (terminal-only outposts have no orbit, etc.).
    /// </summary>
    private static (int Id, RouteScope Scope) ResolveScopeId(UexTerminal terminal, RouteScope requested)
    {
        return requested switch
        {
            RouteScope.Planet when terminal.IdPlanet > 0 => (terminal.IdPlanet, RouteScope.Planet),
            RouteScope.Orbit when terminal.IdOrbit > 0 => (terminal.IdOrbit, RouteScope.Orbit),
            // Fall-through: requested is Planet but no IdPlanet → try Orbit; or
            // requested is Orbit but no IdOrbit → use Terminal.
            RouteScope.Planet when terminal.IdOrbit > 0 => (terminal.IdOrbit, RouteScope.Orbit),
            _ => (terminal.Id, RouteScope.Terminal),
        };
    }

    private static string? ResolveScopeLabel(UexTerminal terminal, RouteScope scope) => scope switch
    {
        RouteScope.Planet => string.IsNullOrWhiteSpace(terminal.PlanetName) ? null : $"Planet: {terminal.PlanetName}",
        RouteScope.Orbit => string.IsNullOrWhiteSpace(terminal.OrbitName) ? null : $"Orbit: {terminal.OrbitName}",
        _ => $"Terminal: {terminal.RichDisplayName}",
    };

    private void ReloadVehicles()
    {
        // Snapshot the selected vehicle id BEFORE the clear so we can
        // re-attach to the equivalent instance in the new collection.
        var previousId = SelectedVehicle?.Id ?? _prefs.RoutesSelectedVehicleId;

        // CRITICAL: the persist guard MUST cover the WHOLE swap, not just the
        // re-selection step.
        //
        // Background: the Vehicles ObservableCollection is bound to the WPF
        // ComboBox via ItemsSource, with SelectedItem={Binding SelectedVehicle,
        // Mode=TwoWay}. When we call Vehicles.Clear() the ComboBox's previously
        // selected reference disappears from the collection — WPF reacts by
        // pushing SelectedItem = null back through the binding. That assignment
        // hits OnSelectedVehicleChanged(null), which calls PersistRoutesPreferences,
        // which writes RoutesSelectedVehicleId = null to prefs.json. The
        // subsequent re-selection IS suppressed, so the on-disk state never
        // gets repaired and the user's vehicle is silently lost on the next
        // app launch.
        //
        // This bug only triggers when the vehicles cache has expired (TTL 24h)
        // — that's exactly when ReloadVehicles is invoked from the Refreshed
        // event after the user navigated to the page (vs the constructor call
        // where no binding is active yet). Wrapping the whole method in the
        // guard kills both paths in one shot.
        _suppressPersist = true;
        try
        {
            Vehicles.Clear();
            foreach (var v in _vehicles.CargoVehicles) Vehicles.Add(v);

            if (previousId is { } id)
            {
                SelectedVehicle = Vehicles.FirstOrDefault(v => v.Id == id);
            }
        }
        finally
        {
            _suppressPersist = false;
        }
    }

    private void ReloadFromProvider()
    {
        AllProposals.Clear();
        foreach (var p in _provider.Proposals) AllProposals.Add(p);
        TotalCount = AllProposals.Count;
        LastRefreshedAt = _provider.LastRefreshedAt;
        // Vehicle cap on top of provider's budget cap. The provider only knows
        // about the investment cap (UEX query), so the SCU column would
        // otherwise show e.g. 2,116 SCU even on a 696-SCU C2 — re-cap here so
        // the visible quantity is always a value the selected ship can lift.
        ApplyVehicleCapToProposals();
        RecomputeAllScores();
        View.Refresh();
        UpdateFilteredCount();
    }

    /// <summary>
    /// Re-applies the budget + vehicle SCU cap to every cached proposal. Mirrors
    /// the formula in <c>TradeRouteProvider.ApplyBudgetProjection</c> and adds the
    /// selected vehicle's cargo capacity as a third <c>min()</c> term so SCU,
    /// Invested, Profit and ETA-derived metrics all reflect what the user can
    /// actually fly out of the origin in one trip. Idempotent — recomputes from
    /// <see cref="UexCommodityRoute.ScuOrigin"/> + prices each call so toggling
    /// the vehicle off restores the budget-only projection.
    /// </summary>
    private void ApplyVehicleCapToProposals()
    {
        int? vehicleCapScu = SelectedVehicle is { Scu: > 0 } v
            ? (int)Math.Floor(v.Scu)
            : null;
        long? investmentCap = InvestmentMaxUec is { } i && i > 0 ? i : null;

        foreach (var p in AllProposals)
        {
            var maxScu = (int)Math.Floor(p.Route.ScuOrigin);
            if (maxScu <= 0)
            {
                p.EffectiveScu = 0;
                p.EffectiveInvestment = 0;
                p.EffectiveProfit = 0;
                continue;
            }

            int eff = maxScu;
            if (investmentCap is { } cap && p.Route.PriceOrigin > 0)
            {
                eff = Math.Min(eff, (int)Math.Floor(cap / p.Route.PriceOrigin));
            }
            if (vehicleCapScu is { } vs)
            {
                eff = Math.Min(eff, vs);
            }
            if (eff < 0) eff = 0;

            var marginPerScu = p.Route.PriceDestination - p.Route.PriceOrigin;
            p.EffectiveScu = eff;
            p.EffectiveInvestment = eff * p.Route.PriceOrigin;
            p.EffectiveProfit = eff * marginPerScu;
        }
    }

    /// <summary>
    /// Container-size compatibility check + score-aware filter wrapper. Routes
    /// whose <see cref="UexCommodityRoute.ContainerSizesOrigin"/> set is disjoint
    /// from the selected vehicle's container set are hidden; routes (or vehicles)
    /// that don't declare any constraint are kept.
    /// </summary>
    private bool FilterPredicate(object o)
    {
        if (o is not TradeRouteProposal p) return false;

        if (SelectedVehicle is { } v)
        {
            var allowed = v.ParsedContainerSizes();
            if (allowed.Count > 0)
            {
                var routeSizes = ParseContainerSizes(p.Route.ContainerSizesOrigin);
                if (routeSizes.Count > 0 && !routeSizes.Overlaps(allowed)) return false;
            }
        }

        // Pill toggle filters — mirror the UEX trade-routes page behaviour.
        // Each ON toggle ANDs a constraint into the predicate; an OFF toggle
        // is a no-op. "Endpoint" flags require BOTH origin AND destination to
        // match — this matches typical hauler intent ("ground-to-ground only",
        // "auto-load both ends") and keeps the filtered set predictable when
        // multiple toggles are combined.
        var r = p.Route;
        if (FilterLoadingDock && (r.HasLoadingDockOrigin != 1 || r.HasLoadingDockDestination != 1)) return false;
        if (FilterFreightElevator && (r.HasFreightElevatorOrigin != 1 || r.HasFreightElevatorDestination != 1)) return false;
        if (FilterMonitored && (r.IsMonitoredOrigin != 1 || r.IsMonitoredDestination != 1)) return false;
        if (FilterSpace && (r.IsSpaceStationOrigin != 1 || r.IsSpaceStationDestination != 1)) return false;
        if (FilterGround && (r.IsOnGroundOrigin != 1 || r.IsOnGroundDestination != 1)) return false;
        if (FilterRefuel && (r.HasRefuelOrigin != 1 || r.HasRefuelDestination != 1)) return false;

        // "Legal" needs a catalog lookup — UEX flags illegality on the
        // commodity, not on each route. A null lookup means the catalog
        // hasn't loaded yet (or the commodity was decommissioned); we
        // conservatively show the row so we don't hide legitimate routes
        // because of a transient catalog miss.
        if (FilterLegal)
        {
            var commodity = _catalog.GetCommodity(r.IdCommodity);
            if (commodity is { IsIllegal: 1 }) return false;
        }

        // "Predicted" = at least one side has a predicted price (no user
        // reports backing it). Useful for datarunners targeting routes
        // where their submission would actually move the needle. Older
        // cached routes may have 0 on both sides because the schema
        // predates user-rows tracking; we don't try to disambiguate, the
        // user can clear the toggle if it produces too many false positives.
        if (FilterPredicted && r.PriceOriginUsersRows > 0 && r.PriceDestinationUsersRows > 0) return false;

        // HARD RULE — never surface negative-profit routes. The budget cap
        // can produce them when the user's investment can only afford a
        // micro-load that doesn't even cover the per-SCU price spread (or
        // when UEX still ranks a route where the destination price has
        // crashed since the algo last ran). Regardless of cause, a negative
        // run is never actionable, so we drop it unconditionally — no
        // toggle, no surprise. The DataGrid column for Profit is the most
        // glanced-at signal on this screen and a wall of red numbers
        // actively misleads the user.
        if (p.EffectiveProfit <= 0) return false;

        // Numeric thresholds — pure trader-side filters that UEX doesn't
        // expose. Both default to null = no constraint; the user types a
        // value in the filter bar and it sticks across sessions.
        if (MinProfit is { } mp && mp > 0 && p.EffectiveProfit < mp) return false;
        if (MinProfitPerMinute is { } mpm && mpm > 0 && p.ProfitPerMinute < mpm) return false;

        return true;
    }

    private static IReadOnlySet<int> ParseContainerSizes(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return new HashSet<int>();
        var set = new HashSet<int>();
        foreach (var token in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (int.TryParse(token, out var n) && n > 0) set.Add(n);
        }
        return set;
    }

    private void UpdateFilteredCount()
    {
        var n = 0;
        foreach (var _ in View) n++;
        FilteredCount = n;
    }

    /// <summary>
    /// Recomputes <see cref="TradeRouteProposal.DatarunnerScore"/> on every row
    /// using the current slider weight. Done in place so the
    /// <see cref="ICollectionView"/>'s sort comparer picks up the new ordering
    /// on the next <see cref="ICollectionView.Refresh"/>.
    ///
    /// Slider semantics:
    ///   0%   (Trader pur)     → score = normProfit
    ///   100% (Datarunner pur) → score = 0.7 × normAge + 0.3 × normCount
    ///                           (priorité à l'ancienneté du prix le plus vieux,
    ///                            avec une part secondaire pour le nombre de
    ///                            commodités à mettre à jour)
    ///   Entre les deux        → interpolation linéaire des deux extrêmes.
    /// </summary>
    private void RecomputeAllScores()
    {
        var w = Math.Clamp(DatarunnerSliderValue / 100.0, 0.0, 1.0);
        foreach (var p in AllProposals)
        {
            // 5M aUEC == score saturation for trader profit on a single run.
            // Above that the curve flattens so a 20M outlier doesn't drown the
            // rest of the list. Uses EffectiveProfit (budget-capped) so the
            // ranking matches what the user can ACTUALLY make on this trip,
            // not the theoretical full-stock profit.
            var normProfit = Math.Min(1.0, Math.Max(0.0, p.EffectiveProfit / 5_000_000.0));

            // 180 days == full saturation for the AGE axis. Anything older than
            // ~6 months is already "ancient" in UEX terms; at that point an extra
            // day doesn't move the needle much. Older rows still rank higher
            // because the slider ALSO blends in count below.
            //
            // We score on terminal-wide MAX age (any commodity), not the
            // route-specific age shown in the badge: visiting a terminal
            // refreshes EVERY price tracked there, so a 3-day-old LARA at a
            // terminal with a 250-day-old Methane row is still huge datarunner
            // value. The badge intentionally shows the narrower number that
            // matches what UEX displays for THIS trade.
            var normAge = Math.Min(1.0, p.MaxDaysStaleAnyCommodity / 180.0);

            // 10 stale rows == saturation for the COUNT axis. Visiting a terminal
            // with 10+ refreshable rows is already maximum-value information-wise.
            var normCount = Math.Min(1.0, p.TotalStale / 10.0);

            // Datarunner side mixes age (primary) and count (secondary). A 320-day
            // route with 2 rows beats a 31-day route with 8 rows — matches what a
            // human would actually want to refresh.
            var datarunnerValue = 0.7 * normAge + 0.3 * normCount;

            p.DatarunnerScore = (1.0 - w) * normProfit + w * datarunnerValue;
        }
    }

    /// <summary>
    /// Refilter the origin suggestion list as the user types — but ONLY when the
    /// text genuinely diverges from the currently-selected terminal. After a user
    /// picks an item from the dropdown, WPF first sets <see cref="SelectedOrigin"/>
    /// then auto-syncs <see cref="OriginSearch"/> to its <c>RichDisplayName</c>;
    /// a refilter at THAT point would remove non-matching items from the bound
    /// <see cref="ObservableCollection{T}"/> and the editable ComboBox would null
    /// out its <c>SelectedItem</c> as a side-effect — which the user perceives as
    /// "the box went empty after I picked an item".
    /// </summary>
    partial void OnOriginSearchChanged(string? value)
    {
        if (SelectedOrigin is { } o
            && string.Equals(value?.Trim(), o.RichDisplayName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        UpdateOriginSuggestions();
    }

    partial void OnDestinationSearchChanged(string? value)
    {
        if (SelectedDestination is { } d
            && string.Equals(value?.Trim(), d.RichDisplayName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        UpdateDestinationSuggestions();

        if (string.IsNullOrWhiteSpace(value))
        {
            SelectedDestination = null;
        }
    }

    /// <summary>
    /// User picked an origin → immediately fetch routes for it (non-forced, so
    /// switching back and forth between known origins serves from cache).
    /// </summary>
    partial void OnSelectedOriginChanged(UexTerminal? value)
    {
        // Re-pin the selected terminal in the suggestion bucket so the ComboBox
        // never resolves SelectedItem to null mid-update.
        UpdateOriginSuggestions();

        if (value is null)
        {
            OriginScopeLabel = null;
            return;
        }
        if (!string.Equals(OriginSearch, value.RichDisplayName, StringComparison.OrdinalIgnoreCase))
            OriginSearch = value.RichDisplayName;

        // Recompute the resolved scope label so the UI reflects what UEX will
        // actually filter on (terminal vs orbit vs planet) — this also handles
        // graceful fallbacks when a terminal lacks orbit/planet metadata.
        var (_, resolvedScope) = ResolveScopeId(value, OriginScope);
        OriginScopeLabel = ResolveScopeLabel(value, resolvedScope);

        if (_suppressAutoRefresh) return;
        _ = TriggerRefreshSafelyAsync();
    }

    partial void OnSelectedDestinationChanged(UexTerminal? value)
    {
        UpdateDestinationSuggestions();

        if (value is not null
            && !string.Equals(DestinationSearch, value.RichDisplayName, StringComparison.OrdinalIgnoreCase))
        {
            DestinationSearch = value.RichDisplayName;
        }

        if (_suppressAutoRefresh || SelectedOrigin is null) return;
        _ = TriggerRefreshSafelyAsync();
    }

    partial void OnInvestmentMaxUecChanged(long? value)
    {
        if (_suppressAutoRefresh || SelectedOrigin is null) return;
        _ = TriggerRefreshSafelyAsync();
    }

    partial void OnOriginScopeChanged(RouteScope value)
    {
        if (SelectedOrigin is { } o)
        {
            var (_, resolvedScope) = ResolveScopeId(o, value);
            OriginScopeLabel = ResolveScopeLabel(o, resolvedScope);
        }
        if (_suppressAutoRefresh || SelectedOrigin is null) return;
        _ = TriggerRefreshSafelyAsync();
    }

    partial void OnDestinationScopeChanged(RouteScope value)
    {
        if (_suppressAutoRefresh || SelectedOrigin is null || SelectedDestination is null) return;
        _ = TriggerRefreshSafelyAsync();
    }

    private async Task TriggerRefreshSafelyAsync()
    {
        if (IsRefreshing || SelectedOrigin is null) return;
        IsRefreshing = true;
        try
        {
            var query = BuildQuery();
            var hit = await _provider.SetQueryAndRefreshAsync(query, force: false).ConfigureAwait(true);
            StatusMessage = hit
                ? $"Refreshed from UEX ({DateTimeOffset.Now:HH:mm})"
                : (LastRefreshedAt is { } at ? $"Local cache from {at.ToLocalTime():HH:mm}" : "Loaded.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Auto-refresh after filter change failed");
            StatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    partial void OnSelectedVehicleChanged(UexVehicle? value)
    {
        // Vehicle change is purely client-side: recap SCU/profit by the new
        // ship's capacity, then re-rank (profit-weighted score depends on the
        // capped EffectiveProfit) and re-filter (incompatible container sets
        // are still hidden by FilterPredicate).
        ApplyVehicleCapToProposals();
        RecomputeAllScores();
        View.Refresh();
        UpdateFilteredCount();
        PersistRoutesPreferences();
    }

    // -----------------------------------------------------------------------
    // Pill toggle handlers — purely client-side (no API call). Each one just
    // re-runs the filter predicate over the cached proposals and persists
    // the new state. Routed through a single helper instead of duplicating
    // body 8x: same code path means consistent behaviour and one place to
    // tweak if we ever want to e.g. debounce the save.
    // -----------------------------------------------------------------------
    partial void OnFilterLoadingDockChanged(bool value) => OnRoutesFilterChanged();
    partial void OnFilterFreightElevatorChanged(bool value) => OnRoutesFilterChanged();
    partial void OnFilterLegalChanged(bool value) => OnRoutesFilterChanged();
    partial void OnFilterMonitoredChanged(bool value) => OnRoutesFilterChanged();
    partial void OnFilterSpaceChanged(bool value) => OnRoutesFilterChanged();
    partial void OnFilterGroundChanged(bool value) => OnRoutesFilterChanged();
    partial void OnFilterRefuelChanged(bool value) => OnRoutesFilterChanged();
    partial void OnFilterPredictedChanged(bool value) => OnRoutesFilterChanged();
    partial void OnMinProfitChanged(long? value) => OnRoutesFilterChanged();
    partial void OnMinProfitPerMinuteChanged(long? value) => OnRoutesFilterChanged();

    private void OnRoutesFilterChanged()
    {
        View.Refresh();
        UpdateFilteredCount();
        PersistRoutesPreferences();
    }

    /// <summary>
    /// Toggles the "default sort" star on the column whose <c>SortMemberPath</c>
    /// matches <paramref name="sortMember"/>. Clicking the lit star unsets
    /// the favourite (returns to the DatarunnerScore fallback); clicking an
    /// unlit star promotes that column to default. Direction stays at the
    /// last value the user actually sorted by — we don't reset it on every
    /// star toggle so a user who likes "Profit ascending" keeps that even
    /// after un-favouriting and re-favouriting the column.
    /// </summary>
    [RelayCommand]
    private void ToggleDefaultSort(string? sortMember)
    {
        if (string.IsNullOrWhiteSpace(sortMember)) return;
        DefaultSortMember = string.Equals(DefaultSortMember, sortMember, StringComparison.Ordinal)
            ? null
            : sortMember;
    }

    partial void OnDefaultSortMemberChanged(string? value)
    {
        ApplyDefaultSort();
        PersistRoutesPreferences();
    }

    partial void OnDefaultSortDirectionChanged(ListSortDirection value)
    {
        ApplyDefaultSort();
        PersistRoutesPreferences();
    }

    /// <summary>
    /// Wired from <c>RoutesView.xaml.cs</c> to the DataGrid's
    /// <c>Sorting</c> event so a manual click on a column header captures
    /// the new direction into <see cref="DefaultSortDirection"/> AND
    /// — only when the column is already favourited — keeps the favourite
    /// in sync. We deliberately do NOT auto-favourite columns the user
    /// merely clicks: that would silently override a previously-favourited
    /// column on a single misclick. The user explicitly opts in via the
    /// star icon.
    /// </summary>
    public void NotifyColumnSorted(string? sortMember, ListSortDirection direction)
    {
        if (string.IsNullOrWhiteSpace(sortMember)) return;
        if (string.Equals(DefaultSortMember, sortMember, StringComparison.Ordinal))
        {
            DefaultSortDirection = direction;
        }
    }

    partial void OnDatarunnerSliderValueChanged(double value)
    {
        // Slider only affects sort weight (no API call).
        RecomputeAllScores();
        View.Refresh();
        UpdateFilteredCount();
        PersistRoutesPreferences();
    }

    /// <summary>
    /// Writes the Trade Routes view preferences (vehicle pick, slider
    /// position) back to disk so they survive an app restart. Fire-and-
    /// forget: a save failure here is non-fatal — at worst the user re-
    /// picks their ship next session — so we only log on failure instead
    /// of surfacing an error to the UI. Suppressed during
    /// <see cref="HydrateFromPreferences"/> to prevent a redundant write
    /// of the value we just loaded.
    /// </summary>
    private void PersistRoutesPreferences()
    {
        if (_suppressPersist) return;
        _prefs.RoutesSelectedVehicleId = SelectedVehicle?.Id;
        _prefs.RoutesDatarunnerSliderValue = DatarunnerSliderValue;
        _prefs.RoutesFilterLoadingDock = FilterLoadingDock;
        _prefs.RoutesFilterFreightElevator = FilterFreightElevator;
        _prefs.RoutesFilterLegal = FilterLegal;
        _prefs.RoutesFilterMonitored = FilterMonitored;
        _prefs.RoutesFilterSpace = FilterSpace;
        _prefs.RoutesFilterGround = FilterGround;
        _prefs.RoutesFilterRefuel = FilterRefuel;
        _prefs.RoutesFilterPredicted = FilterPredicted;
        _prefs.RoutesMinProfit = MinProfit;
        _prefs.RoutesMinProfitPerMinute = MinProfitPerMinute;
        _prefs.RoutesDefaultSortMember = DefaultSortMember;
        _prefs.RoutesDefaultSortDirection = DefaultSortDirection == ListSortDirection.Ascending ? 0 : 1;
        _ = SaveRoutesPreferencesAsync();
    }

    private async Task SaveRoutesPreferencesAsync()
    {
        try
        {
            await _prefs.SaveAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist Trade Routes preferences (vehicle/slider).");
        }
    }

    /// <summary>
    /// Tokenized AND search across the catalog terminals — same logic as
    /// <c>ScreenshotEditViewModel.UpdateTerminalSuggestions</c>, kept local to
    /// avoid coupling with that screen.
    /// </summary>
    private void UpdateOriginSuggestions()
        => UpdateSuggestionsInto(OriginSearch, OriginSuggestions, SelectedOrigin);

    private void UpdateDestinationSuggestions()
        => UpdateSuggestionsInto(DestinationSearch, DestinationSuggestions, SelectedDestination);

    /// <summary>
    /// Synchronises a suggestion bucket to the new desired set incrementally.
    ///
    /// CRITICAL: we do NOT <see cref="ObservableCollection{T}.Clear"/> + re-add.
    /// An editable WPF ComboBox bound via <c>SelectedItem</c> + <c>ItemsSource</c>
    /// nulls out its <c>SelectedItem</c> the instant the bound collection raises
    /// a Reset (because the previously-selected reference is momentarily absent).
    /// We must remove obsolete items one-by-one and never let <paramref name="pinned"/>
    /// leave the bucket — otherwise the editable box visually empties itself the
    /// moment the user picks something from the dropdown.
    /// </summary>
    private void UpdateSuggestionsInto(string? query, ObservableCollection<UexTerminal> bucket, UexTerminal? pinned)
    {
        var newItems = new List<UexTerminal>();

        // Pin the currently-selected terminal at index 0 so the ComboBox always
        // resolves SelectedItem against ItemsSource even when the user starts
        // typing a query that wouldn't match the pinned row.
        if (pinned is not null) newItems.Add(pinned);

        IEnumerable<UexTerminal> source = _catalog.CommodityTerminals;
        var q = query?.Trim() ?? "";
        if (q.Length > 0)
        {
            var tokens = q.Split(TerminalQueryTokenSeparators, StringSplitOptions.RemoveEmptyEntries)
                          .Where(t => t.Length >= 2)
                          .ToArray();
            if (tokens.Length > 0)
            {
                source = source.Where(t => tokens.All(tok => MatchesAnyField(t, tok)));
            }
        }
        foreach (var t in source.OrderBy(t => t.RichDisplayName).Take(50))
        {
            if (newItems.Count >= 50) break;
            if (pinned is not null && ReferenceEquals(t, pinned)) continue;
            newItems.Add(t);
        }

        // Incremental sync: drop items no longer in the result set, then add
        // missing ones. Pinned stays put across the whole operation.
        var newSet = new HashSet<UexTerminal>(newItems);
        for (var i = bucket.Count - 1; i >= 0; i--)
        {
            if (!newSet.Contains(bucket[i])) bucket.RemoveAt(i);
        }
        foreach (var t in newItems)
        {
            if (!bucket.Contains(t)) bucket.Add(t);
        }
    }

    private static bool MatchesAnyField(UexTerminal t, string token)
        => Contains(t.DisplayName, token)
        || Contains(t.Name, token)
        || Contains(t.Nickname, token)
        || Contains(t.Code, token)
        || Contains(t.SpaceStationName, token)
        || Contains(t.OutpostName, token)
        || Contains(t.CityName, token)
        || Contains(t.PlanetName, token)
        || Contains(t.StarSystemName, token);

    private static bool Contains(string? s, string q)
        => s is not null && s.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>
    /// Open the UEX trade-routes page deep-linked to the currently selected
    /// route (uses the route's <c>code</c> for an exact match) or, when no
    /// route is supplied, the origin terminal page.
    /// </summary>
    [RelayCommand]
    private void OpenInBrowser(TradeRouteProposal? p)
    {
        try
        {
            string url;
            if (p is not null && !string.IsNullOrWhiteSpace(p.Route.Code))
            {
                url = $"https://uexcorp.space/trade/route?code={Uri.EscapeDataString(p.Route.Code)}";
            }
            else if (SelectedOrigin is not null)
            {
                url = $"https://uexcorp.space/trade/routes?id_terminal_origin={SelectedOrigin.Id}";
            }
            else
            {
                return;
            }
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not open browser for trade route.");
        }
    }

    /// <summary>Hyperlink target for the Origin column. Lands on the UEX page
    /// that shows where the player can BUY this commodity (origin context),
    /// auto-selecting the correct tab so the user doesn't have to switch
    /// between Vente/Achat manually.</summary>
    [RelayCommand]
    private void OpenOriginTerminal(TradeRouteProposal? p)
    {
        if (p is null) return;
        OpenRouteSideInUex(p, isDestination: false);
    }

    /// <summary>Hyperlink target for the Destination column. Lands on the UEX
    /// page showing where the player can SELL this commodity (destination
    /// context), so the right tab is open by default.</summary>
    [RelayCommand]
    private void OpenDestinationTerminal(TradeRouteProposal? p)
    {
        if (p is null) return;
        OpenRouteSideInUex(p, isDestination: true);
    }

    /// <summary>
    /// Picks the most contextual UEX URL for a click on one side of a route:
    ///  1. PREFERRED — commodity-centric page filtered to the right tab:
    ///     <list type="bullet">
    ///       <item>origin → <c>/commodities/info/name/{slug}/tab/locations_buying/</c>
    ///         = "where the player can BUY this commodity" — terminal page would
    ///         require an extra click to reach the equivalent (Vente) section,
    ///         and the section ID is not URL-anchorable on UEX.</item>
    ///       <item>destination → <c>/commodities/info/name/{slug}/tab/locations_selling/</c>
    ///         = "where the player can SELL this commodity".</item>
    ///     </list>
    ///     This deep-link surfaces the exact row the route cares about (this
    ///     terminal) inside the broader market view, which is the most useful
    ///     context for verifying a price/age before a run.
    ///  2. FALLBACK — terminal info page <c>/commodities/locations/info/{slug}</c>
    ///     when the commodity slug is missing (older route caches).
    ///  3. LAST RESORT — search-results page filtered to the terminal id, when
    ///     even the terminal slug is unknown (legacy catalog cache).
    /// </summary>
    private void OpenRouteSideInUex(TradeRouteProposal p, bool isDestination)
    {
        try
        {
            var commoditySlug = p.Route.CommoditySlug;
            string url;

            if (!string.IsNullOrWhiteSpace(commoditySlug))
            {
                var tab = isDestination ? "locations_selling" : "locations_buying";
                url = $"https://uexcorp.space/commodities/info/name/{Uri.EscapeDataString(commoditySlug!)}/tab/{tab}/";
            }
            else
            {
                var idTerminal = isDestination ? p.Route.IdTerminalDestination : p.Route.IdTerminalOrigin;
                var slugFromRoute = isDestination ? p.Route.DestinationTerminalSlug : p.Route.OriginTerminalSlug;
                var slug = !string.IsNullOrWhiteSpace(slugFromRoute)
                    ? slugFromRoute
                    : _catalog.GetTerminal(idTerminal)?.Slug;

                url = !string.IsNullOrWhiteSpace(slug)
                    ? $"https://uexcorp.space/commodities/locations/info/{Uri.EscapeDataString(slug!)}"
                    : $"https://uexcorp.space/trade/routes?id_terminal_origin={idTerminal}";
            }

            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not open browser for trade route side (destination={Dest}).", isDestination);
        }
    }
}
