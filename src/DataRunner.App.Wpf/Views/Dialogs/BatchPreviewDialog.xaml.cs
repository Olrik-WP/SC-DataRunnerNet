using System.Windows;

namespace DataRunner.App.Views.Dialogs;

public partial class BatchPreviewDialog : Window
{
    public BatchPreviewDialog() => InitializeComponent();

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnSendClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
