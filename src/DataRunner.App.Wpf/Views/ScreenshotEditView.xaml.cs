using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DataRunner.App.ViewModels;
using DataRunner.Core.Models;

namespace DataRunner.App.Views;

public partial class ScreenshotEditView : UserControl
{
    /// <summary>
    /// Cached host window so we can subscribe / unsubscribe to its
    /// <see cref="UIElement.PreviewMouseDown"/> event for the
    /// click-outside-popup auto-close. The window pointer is captured once
    /// in <see cref="OnLoaded"/> and released in <see cref="OnUnloaded"/>
    /// to avoid leaking handlers when the screenshot editor is unloaded
    /// (eg. when the user navigates back to the Inbox view).
    /// </summary>
    private Window? _hostWindow;

    public ScreenshotEditView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    // Terminal-suggestions popup: WPF-Popup behavior notes
    // ====================================================
    // The popup is configured with StaysOpen="True" in XAML and we manage its
    // lifecycle entirely from code-behind. We tried StaysOpen="False" (which
    // auto-closes on outside clicks via internal mouse capture) BUT that
    // pattern conflicts with our open-on-focus trigger:
    //   1. User clicks the textbox → keyboard focus shifts → GotKeyboardFocus
    //      fires → we set IsOpen=true → popup opens.
    //   2. The same click chain's mouse-up reaches the popup mid-layout, and
    //      StaysOpen=False's hit-test treats it as "outside the popup" → the
    //      popup auto-closes on its very first frame.
    // Workarounds via Dispatcher.BeginInvoke "fixed" the flicker but disabled
    // the legitimate mouse capture entirely. Then trying LostKeyboardFocus
    // for the close caused a third bug: clicking a suggestion item shifted
    // focus from the textbox to the listbox-item (popup IS focusable in
    // some WPF scenarios), the close was scheduled at Background priority,
    // and the timing race ate the user's pick before
    // PreviewMouseLeftButtonUp could fire PickTerminalCommand.
    //
    // The robust fix (this file) hooks the host window's PreviewMouseDown
    // and treats it as a "click outside" signal. Two key checks gate the
    // close, both required:
    //   - TerminalSearchBox.IsMouseOver — keep popup open while the user
    //     interacts with the search box and its template (clear-X, etc.).
    //   - TerminalSuggestionsPopup.IsMouseOver — keep popup open while the
    //     user clicks on the suggestion list itself. WPF routed events
    //     follow the VISUAL tree, not HWND topology, so even with
    //     AllowsTransparency=True the popup's mouse-down events DO bubble
    //     up to the host window. Without this second check we'd close
    //     the popup mid-click, tearing down the listbox before
    //     PreviewMouseLeftButtonUp could fire PickTerminalCommand —
    //     which is exactly the "I click Pyro and nothing happens" bug.

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _hostWindow = Window.GetWindow(this);
        if (_hostWindow is not null)
        {
            _hostWindow.PreviewMouseDown += HostWindow_PreviewMouseDown;
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_hostWindow is not null)
        {
            _hostWindow.PreviewMouseDown -= HostWindow_PreviewMouseDown;
            _hostWindow = null;
        }
    }

    /// <summary>
    /// Closes the terminal-suggestions popup whenever the user clicks
    /// somewhere on the main form that is NEITHER the search box NOR the
    /// popup content itself. WPF routed events bubble across the popup's
    /// HWND boundary along the visual tree, so this handler fires for
    /// clicks on popup items too — we must explicitly exclude them via
    /// the popup's <c>IsMouseOver</c> or we'd tear down the listbox
    /// mid-click and lose the user's pick.
    /// </summary>
    private void HostWindow_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not ScreenshotEditViewModel vm) return;
        if (!vm.IsTerminalDropDownOpen) return;

        // Click on the search box (or its template children: placeholder,
        // clear-X button, scrollviewer) → keep popup open.
        if (TerminalSearchBox.IsMouseOver) return;

        // Click on the popup content (suggestion list, item, scrollbar) →
        // keep popup open so the EventSetter on ListBoxItem can run
        // PickTerminalCommand on the matching mouse-up event.
        if (TerminalSuggestionsPopup.IsMouseOver) return;

        vm.IsTerminalDropDownOpen = false;
    }

    /// <summary>
    /// Opens the terminal-suggestions popup when the search box receives
    /// keyboard focus. With <c>StaysOpen=True</c> on the popup, we no longer
    /// need the dispatcher-deferral trick that previously broke mouse capture
    /// (see header comment).
    /// </summary>
    private void TerminalSearchBox_GotKeyboardFocus(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ScreenshotEditViewModel vm) return;
        if (vm.TerminalSuggestions.Count == 0) return;
        vm.IsTerminalDropDownOpen = true;
    }

    /// <summary>
    /// Re-opens the popup on a mouse click into a textbox that already has
    /// keyboard focus — eg. when the user picked a terminal (which closed
    /// the popup) and then clicks back inside the box to refine their search.
    /// <see cref="UIElement.GotKeyboardFocus"/> alone only fires on the focus
    /// <i>transition</i>, missing this case.
    /// </summary>
    private void TerminalSearchBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not ScreenshotEditViewModel vm) return;
        if (vm.TerminalSuggestions.Count == 0) return;
        vm.IsTerminalDropDownOpen = true;
    }

    /// <summary>
    /// Closes the popup when the user presses Escape inside the search box.
    /// Keyboard escape hatch for users who don't want to commit a pick.
    /// </summary>
    private void TerminalSearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        if (DataContext is not ScreenshotEditViewModel vm) return;
        if (!vm.IsTerminalDropDownOpen) return;
        vm.IsTerminalDropDownOpen = false;
        e.Handled = true;
    }

    /// <summary>
    /// Routes mouse-wheel events that land on the suggestions popup into the
    /// list's own <see cref="ScrollViewer"/> and STOPS the event from bubbling
    /// up to the form behind. Without this, wheel events on the popup's
    /// transparent HWND boundary fall through and scroll the validation card
    /// underneath instead of the suggestion list — confusing the user.
    /// </summary>
    private void TerminalSuggestionList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not DependencyObject root) return;
        var sv = FindFirstScrollViewer(root);
        if (sv is not null)
        {
            // ListBox's default scroll step is small; *0.5 keeps the feel close
            // to a native ListBox while ensuring even a single notch produces
            // a visible jump on long suggestion lists.
            sv.ScrollToVerticalOffset(sv.VerticalOffset - (e.Delta * 0.5));
        }
        e.Handled = true;
    }

    private static ScrollViewer? FindFirstScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer sv) return sv;
        var n = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < n; i++)
        {
            var found = FindFirstScrollViewer(VisualTreeHelper.GetChild(root, i));
            if (found is not null) return found;
        }
        return null;
    }

    private void TerminalSuggestion_Picked(object sender, RoutedEventArgs e)
    {
        if (sender is not ListBoxItem { DataContext: UexTerminal terminal }) return;
        if (DataContext is ScreenshotEditViewModel vm)
        {
            vm.PickTerminalCommand.Execute(terminal);
        }
    }

    /// <summary>
    /// Opens the source screenshot in a full-screen viewer with zoom and pan.
    /// We deliberately do NOT show the screenshot inline anywhere in the editor:
    /// the inline preview was eating ~40% of the editor width while the actual
    /// image was tiny, so it was pure noise. The viewer is a separate window
    /// that the user opens on demand when they need to verify what the OCR saw.
    /// </summary>
    private void ShowScreenshot_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ScreenshotEditViewModel vm || vm.PreviewImage is not BitmapImage img)
            return;

        var viewer = new ImageViewerWindow(img)
        {
            Owner = Window.GetWindow(this),
        };
        viewer.ShowDialog();
    }
}
