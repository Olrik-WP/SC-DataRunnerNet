using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace DataRunner.App.Views;

/// <summary>
/// Full-screen modal screenshot viewer. Most of the zoom/pan logic lives in the
/// reusable <see cref="ScreenshotPanel"/> control; this Window only adds the
/// dialog chrome (Esc to close, close in the viewer toolbar, bottom hint).
/// </summary>
public partial class ImageViewerWindow : Window
{
    public ImageViewerWindow(BitmapImage image)
    {
        InitializeComponent();
        DataContext = image;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key is Key.Escape or Key.Enter)
        {
            Close();
            e.Handled = true;
        }
        base.OnPreviewKeyDown(e);
    }

    private void Viewer_CloseRequested(object sender, RoutedEventArgs e) => Close();
}
