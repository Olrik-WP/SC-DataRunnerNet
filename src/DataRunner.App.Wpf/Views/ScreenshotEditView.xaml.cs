using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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

    private void PreviewImage_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
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
