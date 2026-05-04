using System.Collections.ObjectModel;
using System.ComponentModel;
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
/// View model for the "Stale Targets" page.
///
/// Reads from <see cref="IStaleTargetProvider"/> (which owns the rate-limit-safe
/// cache + manual throttle), and exposes a filterable / sortable collection to
/// the DataGrid.
///
/// API hygiene: this VM does NOT trigger an API call on construction. It loads
/// from the provider's disk cache. A first refresh happens on
/// <see cref="EnsureLoadedAsync"/> (called when the user navigates to the page),
/// and only if the cache is older than the provider's TTL.
/// </summary>
public sealed partial class TargetsViewModel : ObservableObject
{
    private readonly IStaleTargetProvider _provider;
    private readonly ILogger<TargetsViewModel> _logger;
    private readonly Dispatcher _uiDispatcher;

    /// <summary>Backing collection bound to the DataGrid (via <see cref="View"/>).</summary>
    public ObservableCollection<StaleTarget> AllTargets { get; } = new();

    /// <summary>The filtered / sorted view exposed to the UI.</summary>
    public ICollectionView View { get; }

    [ObservableProperty] private bool _isRefreshing;
    [ObservableProperty] private string _statusMessage = "Loading from local cache...";
    [ObservableProperty] private string? _filterText;
    [ObservableProperty] private bool _showOnlyOver30Days;
    [ObservableProperty] private bool _showBuy = true;
    [ObservableProperty] private bool _showSell = true;
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private int _filteredCount;
    [ObservableProperty] private DateTimeOffset? _lastRefreshedAt;

    public TargetsViewModel(IStaleTargetProvider provider, ILogger<TargetsViewModel> logger)
    {
        _provider = provider;
        _logger = logger;
        // Capture the UI dispatcher at construction time. The provider raises
        // Refreshed on whatever thread completed the API call (thread-pool),
        // and we MUST marshal back here before touching ObservableCollection /
        // ICollectionView, otherwise WPF throws "CollectionView does not support
        // changes from a different thread than the Dispatcher".
        _uiDispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

        View = CollectionViewSource.GetDefaultView(AllTargets);
        View.Filter = FilterPredicate;
        View.SortDescriptions.Add(new SortDescription(nameof(StaleTarget.PriorityScore), ListSortDirection.Descending));

        _provider.Refreshed += OnProviderRefreshedFromAnyThread;
        ReloadFromProvider();
    }

    private void OnProviderRefreshedFromAnyThread(object? sender, EventArgs e)
    {
        if (_uiDispatcher.CheckAccess())
            ReloadFromProvider();
        else
            _uiDispatcher.BeginInvoke((Action)ReloadFromProvider);
    }

    /// <summary>
    /// Called by the View / nav when the user opens the Targets page.
    /// Triggers a non-forced refresh — provider will skip the API call if the
    /// cache is still within its TTL.
    /// </summary>
    public async Task EnsureLoadedAsync(CancellationToken ct = default)
    {
        try
        {
            var hit = await _provider.RefreshAsync(force: false, ct).ConfigureAwait(true);
            if (hit) StatusMessage = $"Refreshed from UEX ({DateTimeOffset.Now:HH:mm})";
            else if (LastRefreshedAt is { } at)
                StatusMessage = $"Local cache from {at.ToLocalTime():yyyy-MM-dd HH:mm} (still fresh)";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Targets EnsureLoadedAsync failed");
            StatusMessage = "Could not refresh — showing local cache.";
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsRefreshing) return;
        IsRefreshing = true;
        try
        {
            StatusMessage = "Calling UEX...";
            var hit = await _provider.RefreshAsync(force: true).ConfigureAwait(true);
            if (hit)
            {
                StatusMessage = $"Refreshed from UEX ({DateTimeOffset.Now:HH:mm})";
            }
            else
            {
                // Throttle hit — explain to the user.
                var window = _provider.MinManualRefreshInterval;
                StatusMessage = $"Throttled. Manual refresh is limited to once every {(int)window.TotalMinutes} min.";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Manual stale-targets refresh failed");
            StatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    private void ReloadFromProvider()
    {
        AllTargets.Clear();
        foreach (var t in _provider.Targets) AllTargets.Add(t);
        TotalCount = AllTargets.Count;
        LastRefreshedAt = _provider.LastRefreshedAt;
        View.Refresh();
        UpdateFilteredCount();
    }

    private bool FilterPredicate(object o)
    {
        if (o is not StaleTarget t) return false;

        if (ShowOnlyOver30Days && t.DaysStale < 30) return false;

        if (!ShowBuy && t.Type == StaleTargetType.Buy) return false;
        if (!ShowSell && t.Type == StaleTargetType.Sell) return false;

        if (!string.IsNullOrWhiteSpace(FilterText))
        {
            var needle = FilterText.Trim();
            if (t.TerminalName.Contains(needle, StringComparison.OrdinalIgnoreCase)) return true;
            if (t.CommodityName.Contains(needle, StringComparison.OrdinalIgnoreCase)) return true;
            if (!string.IsNullOrEmpty(t.StarSystemName)
                && t.StarSystemName.Contains(needle, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        return true;
    }

    private void UpdateFilteredCount()
    {
        var n = 0;
        foreach (var _ in View) n++;
        FilteredCount = n;
    }

    partial void OnFilterTextChanged(string? value) => RefreshView();
    partial void OnShowOnlyOver30DaysChanged(bool value) => RefreshView();
    partial void OnShowBuyChanged(bool value) => RefreshView();
    partial void OnShowSellChanged(bool value) => RefreshView();

    private void RefreshView()
    {
        View.Refresh();
        UpdateFilteredCount();
    }
}
