using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DataRunner.Core.Abstractions;

namespace DataRunner.App.ViewModels;

public sealed partial class HistoryViewModel : ObservableObject
{
    private readonly ISubmissionHistory _history;

    public ObservableCollection<SubmissionRecord> Items { get; } = new();

    [ObservableProperty] private SubmissionRecord? _selectedItem;
    [ObservableProperty] private bool _isLoading;

    public HistoryViewModel(ISubmissionHistory history)
    {
        _history = history;
        _ = RefreshAsync();
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        IsLoading = true;
        try
        {
            var rows = await _history.GetAllAsync();
            Items.Clear();
            foreach (var r in rows) Items.Add(r);
        }
        finally
        {
            IsLoading = false;
        }
    }
}
