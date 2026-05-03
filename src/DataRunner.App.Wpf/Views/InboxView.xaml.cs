using System.Windows;
using System.Windows.Controls;

namespace DataRunner.App.Views;

public partial class InboxView : UserControl
{
    public InboxView() => InitializeComponent();

    /// <summary>
    /// Opens the rescan ContextMenu on a left-click of the rescan button.
    /// WPF only opens ContextMenu on right-click by default; for a button
    /// that LOOKS like a dropdown, we trigger it manually here.
    /// </summary>
    private void OnRescanButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.ContextMenu is null) return;

        fe.ContextMenu.PlacementTarget = fe;
        fe.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        fe.ContextMenu.IsOpen = true;
    }
}
