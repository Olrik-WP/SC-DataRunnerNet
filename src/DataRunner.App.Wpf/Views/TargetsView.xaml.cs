using System.Windows.Controls;
using DataRunner.App.ViewModels;

namespace DataRunner.App.Views;

public partial class TargetsView : UserControl
{
    public TargetsView()
    {
        InitializeComponent();

        // Trigger a non-forced refresh whenever this view becomes visible.
        // The provider will skip the API call if its cache is still within TTL,
        // so this is safe to wire on every visibility flip.
        IsVisibleChanged += async (_, e) =>
        {
            if (e.NewValue is true && DataContext is TargetsViewModel vm)
            {
                await vm.EnsureLoadedAsync();
            }
        };
    }
}
