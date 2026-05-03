using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using DataRunner.App.ViewModels;
using DataRunner.Core.Models;

namespace DataRunner.App.Views;

public partial class ScreenshotEditView : UserControl
{
    public ScreenshotEditView() => InitializeComponent();

    private void TerminalSearchBox_GotKeyboardFocus(object sender, RoutedEventArgs e)
    {
        if (DataContext is ScreenshotEditViewModel vm && vm.TerminalSuggestions.Count > 0)
        {
            vm.IsTerminalDropDownOpen = true;
        }
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
