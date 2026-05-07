using System.Windows.Controls;
using DataRunner.App.ViewModels;

namespace DataRunner.App.Views;

public partial class RoutesView : UserControl
{
    public RoutesView()
    {
        InitializeComponent();

        // Trigger a non-forced refresh whenever this view becomes visible.
        // The provider skips the API call if its cache is still within TTL.
        IsVisibleChanged += async (_, e) =>
        {
            if (e.NewValue is true && DataContext is RoutesViewModel vm)
            {
                await vm.EnsureLoadedAsync();
            }
        };
    }

    /// <summary>
    /// Forwards the DataGrid's Sorting event to the view model so a manual
    /// click on a column header can capture the chosen direction back into
    /// <see cref="RoutesViewModel.DefaultSortDirection"/>. We let the
    /// DataGrid handle the actual ordering itself (no e.Handled = true);
    /// we only LISTEN to learn what the user picked. The VM ignores clicks
    /// on non-favourited columns so a stray sort doesn't quietly steal
    /// the favourite.
    /// </summary>
    private void RoutesGrid_OnSorting(object sender, DataGridSortingEventArgs e)
    {
        if (DataContext is not RoutesViewModel vm) return;
        var memberPath = e.Column.SortMemberPath;
        if (string.IsNullOrWhiteSpace(memberPath)) return;

        // The DataGrid fires this event BEFORE flipping the direction; the
        // arrow we're about to see is the OPPOSITE of e.Column.SortDirection
        // when it's already set to a value (= subsequent click on the same
        // header). Compute the resolved direction the same way the grid will.
        var nextDirection = e.Column.SortDirection switch
        {
            System.ComponentModel.ListSortDirection.Ascending => System.ComponentModel.ListSortDirection.Descending,
            System.ComponentModel.ListSortDirection.Descending => System.ComponentModel.ListSortDirection.Ascending,
            _ => System.ComponentModel.ListSortDirection.Ascending,
        };

        vm.NotifyColumnSorted(memberPath, nextDirection);
    }
}
