using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace DataRunner.App.Views;

public partial class ImageViewerWindow : Window
{
    private const double MinScale = 0.1;
    private const double MaxScale = 8.0;
    private const double ZoomStep = 1.25;

    public ImageViewerWindow(BitmapImage image)
    {
        InitializeComponent();
        DataContext = image;
        FitToWindow();
    }

    private void FitToWindow()
    {
        if (DataContext is not BitmapImage img) return;
        Loaded += (_, _) =>
        {
            var availableW = Scroller.ActualWidth - 40;
            var availableH = Scroller.ActualHeight - 80;
            if (availableW <= 0 || availableH <= 0 || img.PixelWidth == 0) return;

            var scale = Math.Min(availableW / img.PixelWidth, availableH / img.PixelHeight);
            scale = Math.Min(scale, 1.0); // never upscale by default
            ImageScale.ScaleX = scale;
            ImageScale.ScaleY = scale;
        };
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

    private void PreviewImage_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        var factor = e.Delta > 0 ? ZoomStep : 1.0 / ZoomStep;
        var newScale = Math.Clamp(ImageScale.ScaleX * factor, MinScale, MaxScale);
        ImageScale.ScaleX = newScale;
        ImageScale.ScaleY = newScale;
        e.Handled = true;
    }

    private void PreviewImage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Single click cycles 100% -> 200% -> fit
        var current = ImageScale.ScaleX;
        if (current < 1.0) ImageScale.ScaleX = ImageScale.ScaleY = 1.0;
        else if (current < 2.0) ImageScale.ScaleX = ImageScale.ScaleY = 2.0;
        else FitToWindowNow();
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e)
    {
        ImageScale.ScaleX = ImageScale.ScaleY =
            Math.Clamp(ImageScale.ScaleX * ZoomStep, MinScale, MaxScale);
    }

    private void ZoomOut_Click(object sender, RoutedEventArgs e)
    {
        ImageScale.ScaleX = ImageScale.ScaleY =
            Math.Clamp(ImageScale.ScaleX / ZoomStep, MinScale, MaxScale);
    }

    private void ZoomReset_Click(object sender, RoutedEventArgs e)
    {
        ImageScale.ScaleX = ImageScale.ScaleY = 1.0;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void FitToWindowNow()
    {
        if (DataContext is not BitmapImage img) return;
        var availableW = Scroller.ActualWidth - 40;
        var availableH = Scroller.ActualHeight - 80;
        if (availableW <= 0 || availableH <= 0 || img.PixelWidth == 0) return;

        var scale = Math.Min(availableW / img.PixelWidth, availableH / img.PixelHeight);
        scale = Math.Min(scale, 1.0);
        ImageScale.ScaleX = scale;
        ImageScale.ScaleY = scale;
    }
}
