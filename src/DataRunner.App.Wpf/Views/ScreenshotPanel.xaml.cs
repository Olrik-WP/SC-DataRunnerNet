using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace DataRunner.App.Views;

/// <summary>
/// Reusable screenshot viewer with zoom (wheel + buttons) and pan (drag).
/// Used by both the full-screen <see cref="ImageViewerWindow"/> and the docked
/// side-by-side panel in <see cref="ScreenshotEditView"/>.
///
/// The image source is exposed as a DependencyProperty so it can be data-bound
/// from a parent view's DataContext (eg. ScreenshotEditViewModel.PreviewImage).
/// Zoom state is per-instance (each side-by-side panel keeps its own zoom).
/// </summary>
public partial class ScreenshotPanel : UserControl
{
    private const double MinScale = 0.05;
    private const double MaxScale = 8.0;
    private const double ZoomStep = 1.25;

    private bool _isDragging;
    private Point _dragStart;
    private double _scrollStartH;
    private double _scrollStartV;

    public static readonly DependencyProperty ImageSourceProperty = DependencyProperty.Register(
        nameof(ImageSource),
        typeof(BitmapImage),
        typeof(ScreenshotPanel),
        new PropertyMetadata(null, OnImageSourceChanged));

    public BitmapImage? ImageSource
    {
        get => (BitmapImage?)GetValue(ImageSourceProperty);
        set => SetValue(ImageSourceProperty, value);
    }

    public static readonly DependencyProperty HeaderLabelProperty = DependencyProperty.Register(
        nameof(HeaderLabel),
        typeof(string),
        typeof(ScreenshotPanel),
        new PropertyMetadata("Screenshot"));

    public string HeaderLabel
    {
        get => (string)GetValue(HeaderLabelProperty);
        set => SetValue(HeaderLabelProperty, value);
    }

    public static readonly DependencyProperty ShowCloseButtonProperty = DependencyProperty.Register(
        nameof(ShowCloseButton),
        typeof(bool),
        typeof(ScreenshotPanel),
        new PropertyMetadata(false));

    /// <summary>When true, shows a close control in the top toolbar (full-screen window only).</summary>
    public bool ShowCloseButton
    {
        get => (bool)GetValue(ShowCloseButtonProperty);
        set => SetValue(ShowCloseButtonProperty, value);
    }

    public static readonly RoutedEvent CloseRequestedEvent = EventManager.RegisterRoutedEvent(
        nameof(CloseRequested),
        RoutingStrategy.Bubble,
        typeof(RoutedEventHandler),
        typeof(ScreenshotPanel));

    public event RoutedEventHandler CloseRequested
    {
        add => AddHandler(CloseRequestedEvent, value);
        remove => RemoveHandler(CloseRequestedEvent, value);
    }

    public ScreenshotPanel()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            // When the control loads for the first time with an image already
            // bound (eg. the editor was created with a PreviewImage set before
            // the panel was rendered), auto-fit the right panel immediately.
            if (ImageSource is not null) FitRightPanel();
        };
    }

    /// <summary>
    /// The fraction of the screenshot's width where the SC right panel starts.
    /// Must match <c>ImagePreprocessor.RightPanelStartFraction</c> (0.55).
    /// Used to auto-scroll to the commodity data on load instead of showing
    /// the cockpit and the player inventory panel (which are never relevant
    /// during review).
    /// </summary>
    private const double RightPanelStartFraction = 0.55;

    private static void OnImageSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScreenshotPanel panel) return;
        if (e.NewValue is null) return;

        if (panel.IsLoaded && panel.Scroller.ActualWidth > 0)
        {
            panel.FitRightPanel();
        }
        else
        {
            // The panel exists but hasn't been measured yet (ActualWidth == 0).
            // This happens on the FIRST auto-open: the OCR finishes →
            // OpenEditorIfReady creates the editor → the binding sets
            // PreviewImage → this callback fires → but the ScreenshotPanel's
            // first layout pass hasn't run yet so we can't compute scroll
            // offsets. Defer to the next Loaded/LayoutUpdated cycle.
            void OnceReady(object? s, EventArgs ev)
            {
                panel.LayoutUpdated -= OnceReady;
                if (panel.ImageSource is not null && panel.Scroller.ActualWidth > 0)
                {
                    panel.FitRightPanel();
                }
            }
            panel.LayoutUpdated += OnceReady;
        }
    }

    /// <summary>
    /// Zooms to fit the RIGHT PANEL of the screenshot (the commodity rows
    /// area) and scrolls so that panel is visible from the start. This is the
    /// zone the user actually needs to verify during review — everything to
    /// the left (cockpit, player inventory) is noise.
    ///
    /// Falls back to a full-image fit when the image is too small or the
    /// panel can't be computed.
    /// </summary>
    private void FitRightPanel()
    {
        if (ImageSource is null) return;
        var availableW = Scroller.ActualWidth - 20;
        var availableH = Scroller.ActualHeight - 60;
        if (availableW <= 0 || availableH <= 0 || ImageSource.PixelWidth == 0)
            return;

        // Compute the right panel's pixel area. We start slightly INSIDE the
        // right panel (60% instead of 55%) to crop the divider line and show
        // only the commodity cards — tighter framing = bigger zoom.
        const double viewStartFraction = 0.60;
        var panelStartX = ImageSource.PixelWidth * viewStartFraction;
        var panelWidth = ImageSource.PixelWidth - panelStartX;
        var panelHeight = (double)ImageSource.PixelHeight;

        // Scale so the right panel fills the available area. No upper cap —
        // if the panel is tiny and the viewport is wide, we zoom in enough
        // to make the text legible.
        var scale = Math.Min(availableW / panelWidth, availableH / panelHeight);
        scale = Math.Max(scale, MinScale);

        ImageScale.ScaleX = scale;
        ImageScale.ScaleY = scale;

        // Defer the scroll to after the layout pass so the ScrollViewer has
        // updated its ScrollableWidth/Height with the new scale. Without this
        // the offset computation uses the old extents and undershoots.
        Dispatcher.InvokeAsync(() =>
        {
            var offsetX = panelStartX * scale;
            Scroller.ScrollToHorizontalOffset(offsetX);
            Scroller.ScrollToVerticalOffset(0);
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>Fits the ENTIRE image into the viewport (zoom-to-overview).</summary>
    private void FitToWindow()
    {
        if (ImageSource is null) return;
        var availableW = Scroller.ActualWidth - 20;
        var availableH = Scroller.ActualHeight - 60;
        if (availableW <= 0 || availableH <= 0 || ImageSource.PixelWidth == 0) return;

        var scale = Math.Min(availableW / ImageSource.PixelWidth, availableH / ImageSource.PixelHeight);
        scale = Math.Min(scale, 1.0);
        ImageScale.ScaleX = scale;
        ImageScale.ScaleY = scale;
        Scroller.ScrollToHorizontalOffset(0);
        Scroller.ScrollToVerticalOffset(0);
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
        // Click-and-drag pans the scrollable area when zoomed in — much more
        // useful in a docked side panel than the previous zoom-cycle (which
        // the wheel already covers). We capture the mouse so drags that
        // overshoot the image bounds still track.
        _isDragging = true;
        _dragStart = e.GetPosition(Scroller);
        _scrollStartH = Scroller.HorizontalOffset;
        _scrollStartV = Scroller.VerticalOffset;
        ((UIElement)sender).CaptureMouse();
        e.Handled = true;
    }

    private void PreviewImage_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging) return;
        _isDragging = false;
        ((UIElement)sender).ReleaseMouseCapture();
        e.Handled = true;
    }

    private void PreviewImage_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging) return;
        var pos = e.GetPosition(Scroller);
        var deltaX = _dragStart.X - pos.X;
        var deltaY = _dragStart.Y - pos.Y;
        Scroller.ScrollToHorizontalOffset(_scrollStartH + deltaX);
        Scroller.ScrollToVerticalOffset(_scrollStartV + deltaY);
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

    private void FitRightPanel_Click(object sender, RoutedEventArgs e) => FitRightPanel();
    private void FitToWindow_Click(object sender, RoutedEventArgs e) => FitToWindow();

    private void CloseToolbar_Click(object sender, RoutedEventArgs e) =>
        RaiseEvent(new RoutedEventArgs(CloseRequestedEvent, this));
}
