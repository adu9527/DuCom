using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DuCom.Core.Ports;
using DuCom.Core.Sessions;
using DuCom.Services;

namespace DuCom.ViewModels;

public partial class SessionViewModel
{
    private const int MaximumWarnings = 50;
    private string _lastLoggedFault = string.Empty;
    private object? _dismissedFault;

    [ObservableProperty]
    public partial bool IsFaultWarningVisible { get; private set; }

    [RelayCommand]
    private void DismissFaultWarning()
    {
        _dismissedFault = _session.Status.Fault;
        IsFaultWarningVisible = false;
    }

    public ObservableCollection<string> Warnings { get; } = [];

    [ObservableProperty]
    public partial bool HasFault { get; private set; }

    [ObservableProperty]
    public partial string FaultMessage { get; private set; } = string.Empty;

    private static string GetResourceString(string key) =>
        Application.Current?.TryFindResource(key) as string ?? key;

    /// <summary>Surfaces a watchdog hint in the session warning surface (UI thread).</summary>
    public void RaiseWatchdogHint(string hint) =>
        OnSessionWarning(this, new SessionWarningEventArgs(hint));

    [RelayCommand]
    private void ClearWarnings()
    {
        Warnings.Clear();
    }

    private void OnSessionWarning(object? sender, SessionWarningEventArgs e)
    {
        Dispatcher dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        if (!dispatcher.CheckAccess())
        {
            _ = dispatcher.BeginInvoke(() => OnSessionWarning(sender, e), DispatcherPriority.Background);
            return;
        }

        Warnings.Insert(0, e.Warning);
        while (Warnings.Count > MaximumWarnings)
        {
            Warnings.RemoveAt(Warnings.Count - 1);
        }
    }

    private bool RefreshState()
    {
        PortLifecycleState previousState = State;
        bool previousIsOpen = IsOpen;
        bool previousHasFault = HasFault;
        string previousFaultMessage = FaultMessage;
        DuCom.Core.Sessions.SerialSessionStatusSnapshot snapshot = _session.Status;
        PortLifecycleSnapshot state = snapshot.State;
        State = state.State;
        IsOpen = state.State == PortLifecycleState.Open;
        HasFault = snapshot.Fault is not null;
        if (!HasFault) _dismissedFault = null;
        IsFaultWarningVisible = HasFault && !ReferenceEquals(snapshot.Fault, _dismissedFault);
        string diagnosticFault = snapshot.Fault?.Message ?? string.Empty;
        FaultMessage = GetUserFaultMessage(diagnosticFault);
        if (HasFault && !string.Equals(_lastLoggedFault, FaultMessage, StringComparison.Ordinal))
        {
            _lastLoggedFault = FaultMessage;
            Program.DiagnosticLog?.Error($"Session fault. Port={PortName}; Source={snapshot.Fault?.Source}; Message={diagnosticFault}");
        }
        if (previousIsOpen && !IsOpen && HasFault)
            ConnectionFaulted?.Invoke(this);
        return previousState != State ||
            previousIsOpen != IsOpen ||
            previousHasFault != HasFault ||
            !string.Equals(previousFaultMessage, FaultMessage, StringComparison.Ordinal);
    }

    internal static string GetUserFaultMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return string.Empty;

        if (message.Contains("does not resolve to a valid serial port", StringComparison.OrdinalIgnoreCase))
        {
            return GetResourceString("Status.PortUnavailable");
        }

        if (message.Contains("UnauthorizedAccessException", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("denied", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("拒绝访问", StringComparison.Ordinal))
        {
            return GetResourceString("Status.PortOccupied");
        }

        if (message.Contains("TimeoutException", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("超时", StringComparison.Ordinal))
            return GetResourceString("Status.PortTimeout");

        if (message.Contains("ArgumentException", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("ArgumentOutOfRangeException", StringComparison.OrdinalIgnoreCase))
            return GetResourceString("Status.InvalidPortSettings");

        if (message.Contains("does not exist", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("不存在", StringComparison.Ordinal) ||
            message.Contains("找不到", StringComparison.Ordinal))
            return GetResourceString("Status.PortUnavailable");

        return GetResourceString("Status.PortConnectionFault");
    }
}
