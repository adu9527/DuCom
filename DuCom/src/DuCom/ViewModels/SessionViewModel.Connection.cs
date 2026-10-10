using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DuCom.Core.Parsing;
using DuCom.Core.Ports;
using DuCom.Core.Sessions;

namespace DuCom.ViewModels;

public partial class SessionViewModel
{
    private readonly SerialReconnectIntent _reconnectIntent = new();
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

    internal Func<SessionViewModel, Task>? ReopenRequested { get; set; }

    public async Task<PortCommandResult> OpenAsync(CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        try
        {
            PortCommandResult result = await _session.OpenAsync(cancellationToken);
            if (result == PortCommandResult.Succeeded)
            {
                _reconnectIntent.Opened();
                IsWaitingForReconnect = false;
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

    public async Task CloseAsync(CancellationToken cancellationToken = default) =>
        await CloseCoreAsync(deviceRemoved: false, cancellationToken);

    internal Task CloseRemovedDeviceAsync() => CloseCoreAsync(deviceRemoved: true, CancellationToken.None);

    private async Task CloseCoreAsync(bool deviceRemoved, CancellationToken cancellationToken)
    {
        if (!deviceRemoved)
        {
            _reconnectIntent.Cancel();
            IsWaitingForReconnect = false;
        }
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
        if (IsBusy)
        {
            return;
        }

        _reconnectIntent.Cancel();
        IsWaitingForReconnect = false;
        if (IsOpen)
        {
            await CloseAsync();
        }
        else if (ReopenRequested is not null)
        {
            await ReopenRequested(this);
        }
        else
        {
            await OpenAsync();
        }
    }

    internal bool ShouldHandleDeviceRemoval => _reconnectIntent.ShouldHandleRemoval(IsOpen);

    internal void MarkDeviceRemoved()
    {
        _reconnectIntent.Removed(AutoReconnect);
        IsWaitingForReconnect = _reconnectIntent.Waiting;
    }

    internal void RestoreReconnectWait()
    {
        _reconnectIntent.RetryPending();
        IsWaitingForReconnect = true;
    }

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
