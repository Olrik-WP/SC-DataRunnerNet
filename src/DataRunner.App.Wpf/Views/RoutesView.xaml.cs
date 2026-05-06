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
}
