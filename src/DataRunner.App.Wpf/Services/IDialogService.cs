using System.Windows;
using DataRunner.App.ViewModels;

namespace DataRunner.App.Services;

public interface IDialogService
{
    Task<bool> ShowConfirmSubmitAsync(ConfirmSubmitViewModel vm);

    /// <summary>
    /// Mandatory pre-flight for every batch send. Surfaces the smart-split
    /// plan as a per-commodity table so the user can spot mistakes before
    /// any /data_submit POST. Returns true iff the user clicked "Send all".
    /// </summary>
    Task<bool> ShowBatchPreviewAsync(BatchPreviewViewModel vm);

    void ShowError(string title, string message);
    void ShowInfo(string title, string message);
}

public sealed class DialogService : IDialogService
{
    public Task<bool> ShowConfirmSubmitAsync(ConfirmSubmitViewModel vm)
    {
        var dlg = new Views.Dialogs.ConfirmSubmitDialog
        {
            DataContext = vm,
            Owner = Application.Current.MainWindow,
        };
        var result = dlg.ShowDialog();
        return Task.FromResult(result == true);
    }

    public Task<bool> ShowBatchPreviewAsync(BatchPreviewViewModel vm)
    {
        var dlg = new Views.Dialogs.BatchPreviewDialog
        {
            DataContext = vm,
            Owner = Application.Current.MainWindow,
        };
        var result = dlg.ShowDialog();
        return Task.FromResult(result == true);
    }

    public void ShowError(string title, string message)
        => MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);

    public void ShowInfo(string title, string message)
        => MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);
}
