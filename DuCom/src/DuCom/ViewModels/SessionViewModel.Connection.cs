using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DuCom.Core.Parsing;
using DuCom.Core.Ports;
using DuCom.Core.Sessions;

namespace DuCom.ViewModels;

public partial class SessionViewModel
{
    [ObservableProperty]
    public partial PortLifecycleState State { get; private set; }

    [ObservableProperty]
    public partial bool IsOpen { get; private set; }

    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    public partial bool IsInRightPane { get; set; }

    [ObservableProperty]
    public partial bool AutoReconnect { get; set; }

    [ObservableProperty]
    public partial bool IsWaitingForReconnect { get; private set; }

    [ObservableProperty]
    public partial ReceiveDisplayMode ReceiveMode { get; set; }

    [ObservableProperty]
    public partial bool TimestampEnabled { get; set; }

    [ObservableProperty]
    public partial bool LoggingEnabled { get; set; }

    public async Task<PortCommandResult> OpenAsync(CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        try
        {
            PortCommandResult result = await _session.OpenAsync(cancellationToken);
            if (result == PortCommandResult.Succeeded)
            {
                FollowEnd = true;
            }
            return result;
        }
        finally
        {
            IsBusy = false;
            RefreshState();
            OnPropertyChanged(nameof(BaudRate));
        }
    }

    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        try
        {
            await _session.CloseAsync(cancellationToken);
        }
        finally
        {
            IsBusy = false;
            RefreshState();
            OnPropertyChanged(nameof(BaudRate));
        }
    }

    [RelayCommand]
    private async Task ToggleConnectionAsync()
    {
        IsWaitingForReconnect = false;
        if (IsBusy)
        {
            return;
        }

        if (IsOpen)
        {
            await CloseAsync();
        }
        else
        {
            await OpenAsync();
        }
    }

    internal void MarkDeviceRemoved() => IsWaitingForReconnect = AutoReconnect;

    internal void ClearReconnectWait() => IsWaitingForReconnect = false;

    public async Task ApplySettingsAsync(SerialPortSettings settings, CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        try
        {
            await _session.ApplySettingsAsync(settings, cancellationToken);
        }
        finally
        {
            IsBusy = false;
            RefreshState();
            OnPropertyChanged(nameof(BaudRate));
        }
    }
}
