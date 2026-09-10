using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DuCom.ViewModels;

public partial class ToolCenterViewModel
{
    public ObservableCollection<string> SendHistoryList { get; } = [];

    [ObservableProperty]
    public partial string SendHistorySearchText { get; set; } = string.Empty;

    partial void OnSendHistorySearchTextChanged(string value) => RefreshSendHistoryList();

    internal void RefreshSendHistoryList()
    {
        SendHistoryList.Clear();
        if (_mainViewModel is null)
        {
            return;
        }

        foreach (string entry in _mainViewModel.SearchSendHistoryEntries(SendHistorySearchText))
        {
            SendHistoryList.Add(entry);
        }
    }

    [RelayCommand]
    private void UseSendHistoryEntry(string? entry)
    {
        if (!string.IsNullOrEmpty(entry))
        {
            _mainViewModel?.UseSendHistoryEntry(entry);
        }
    }

    [RelayCommand]
    private void DeleteSendHistoryEntry(string? entry)
    {
        if (string.IsNullOrEmpty(entry))
        {
            return;
        }

        _mainViewModel?.DeleteSendHistoryEntry(entry);
        RefreshSendHistoryList();
    }

    [RelayCommand]
    private void ClearSendHistory()
    {
        _mainViewModel?.ClearSendHistory();
        RefreshSendHistoryList();
    }
}
