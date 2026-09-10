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
        string diagnosticFault = snapshot.Fault?.Message ?? string.Empty;
        FaultMessage = GetUserFaultMessage(diagnosticFault);
        if (HasFault && !string.Equals(_lastLoggedFault, FaultMessage, StringComparison.Ordinal))
        {
            _lastLoggedFault = FaultMessage;
            Program.DiagnosticLog?.Error($"Session fault. Port={PortName}; Source={snapshot.Fault?.Source}; Message={diagnosticFault}");
        }
        return previousState != State ||
            previousIsOpen != IsOpen ||
            previousHasFault != HasFault ||
            !string.Equals(previousFaultMessage, FaultMessage, StringComparison.Ordinal);
    }

    private static string GetUserFaultMessage(string message)
    {
        if (message.Contains("does not resolve to a valid serial port", StringComparison.OrdinalIgnoreCase))
        {
            return GetResourceString("Status.PortUnavailable");
        }

        if (message.Contains("Access to the path", StringComparison.OrdinalIgnoreCase) &&
            message.Contains("denied", StringComparison.OrdinalIgnoreCase))
        {
            return GetResourceString("Status.PortOccupied");
        }

        return message;
    }
}
