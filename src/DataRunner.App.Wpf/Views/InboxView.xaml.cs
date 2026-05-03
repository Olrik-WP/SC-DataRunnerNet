using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using DataRunner.App.ViewModels;

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

    /// <summary>
    /// Click on an inbox-card thumbnail opens the FULL screenshot in the
    /// existing <see cref="ImageViewerWindow"/>. We re-decode at full
    /// resolution here (the thumbnail is downscaled to 96 px) so the user
    /// sees the original quality for verification.
    /// </summary>
    private void Thumbnail_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: InboxItem item }) return;
        if (string.IsNullOrWhiteSpace(item.ImagePath) || !File.Exists(item.ImagePath)) return;

        try
        {
            var full = new BitmapImage();
            full.BeginInit();
            full.CacheOption = BitmapCacheOption.OnLoad;
            full.UriSource = new System.Uri(item.ImagePath);
            full.EndInit();
            full.Freeze();

            var win = new ImageViewerWindow(full)
            {
                Owner = Window.GetWindow(this),
                Title = item.DisplayName,
            };
            win.ShowDialog();
            // Don't propagate to the ListBox so the click doesn't also change selection.
            e.Handled = true;
        }
        catch
        {
            // Silent: the user just clicked a preview, swallowing a transient
            // I/O error (file moved/deleted between now and decode) is fine.
        }
    }
}
