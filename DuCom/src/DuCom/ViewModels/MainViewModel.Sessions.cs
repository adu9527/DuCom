using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using DuCom.Core.Parsing;
using DuCom.Core.Ports;
using DuCom.Core.Persistence;
using DuCom.Core.Sending;
using CommunityToolkit.Mvvm.Input;
using DuCom.Services;

namespace DuCom.ViewModels;

public partial class MainViewModel
{
    partial void OnSelectedSessionChanged(SessionViewModel? value)
    {
        if (value is not null)
        {
            _activeLogSession = value;
        }

        _sendHistoryNavigator.Reset();
        NotifyCommandStates();
    }

    partial void OnSelectedRightSessionChanged(SessionViewModel? value)
    {
        if (value is not null)
        {
            _activeLogSession = value;
        }
    }

    /// <summary>Activates the session whose log surface the user is interacting with.</summary>
    internal void ActivateLogSession(SessionViewModel session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _activeLogSession = session;
        if (RightSessions.Contains(session))
        {
            SelectedRightSession = session;
        }
        else if (Sessions.Contains(session))
        {
            SelectedSession = session;
        }
        else
        {
            return;
        }
    }

    internal SessionViewModel? ActiveSession => _activeLogSession ?? SelectedSession ?? SelectedRightSession;

    internal void ClearActiveDisplay() => ActiveSession?.ClearDisplay();

    internal void ToggleActiveFollowEnd()
    {
        if (ActiveSession is { } session)
        {
            session.FollowEnd = !session.FollowEnd;
        }
    }

    internal void ToggleActiveReceiveMode()
    {
        if (ActiveSession is { } session)
        {
            session.ReceiveMode = session.ReceiveMode == ReceiveDisplayMode.Str ? ReceiveDisplayMode.Hex : ReceiveDisplayMode.Str;
            RememberPortOverride(session.PortName);
        }
        else
        {
            ReceiveMode = ReceiveMode == ReceiveDisplayMode.Str ? ReceiveDisplayMode.Hex : ReceiveDisplayMode.Str;
        }

        StatusMessage = GetResourceString("Status.SessionSettingRequiresReopen");
    }

    internal void ToggleActiveTimestamp()
    {
        if (ActiveSession is { } session)
        {
            session.TimestampEnabled = !session.TimestampEnabled;
            RememberPortOverride(session.PortName);
        }
        else
        {
            TimestampEnabled = !TimestampEnabled;
        }

        StatusMessage = GetResourceString("Status.SessionSettingRequiresReopen");
    }

    partial void OnSelectedPortItemChanged(PortItemViewModel? value)
    {
        OnPropertyChanged(nameof(LogFileNamePreview));
        SessionViewModel? session = Sessions.FirstOrDefault(
            item => string.Equals(item.PortName, value?.PortName, StringComparison.OrdinalIgnoreCase));
        if (session is not null)
        {
            if (RightSessions.Contains(session))
            {
                return;
            }

            SelectedSession = session;
        }
        if (value is not null && session is null)
        {
            session = CreateClosedSession(value.PortName);
            Sessions.Add(session);
            SelectedSession = session;
        }
    }

    internal Task AssignRightPaneAsync(string portName)
    {
        SessionViewModel? primarySession = SelectedSession;
        SessionViewModel? session = Sessions.FirstOrDefault(item =>
            string.Equals(item.PortName, portName, StringComparison.OrdinalIgnoreCase));
        if (session is null)
        {
            session = CreateClosedSession(portName);
            Sessions.Add(session);
        }

        SessionViewModel? leftSession = primarySession is not null && !ReferenceEquals(primarySession, session)
            && !RightSessions.Contains(primarySession)
            ? primarySession
            : Sessions.FirstOrDefault(item => item.IsOpen && !ReferenceEquals(item, session) && !RightSessions.Contains(item))
                ?? Sessions.FirstOrDefault(item => !ReferenceEquals(item, session) && !RightSessions.Contains(item));
        if (leftSession is null)
        {
            StatusMessage = GetResourceString("Status.SplitNeedsAnotherSession");
            return Task.CompletedTask;
        }

        session.IsInRightPane = true;
        if (!RightSessions.Contains(session))
        {
            RightSessions.Add(session);
        }
        SelectedRightSession = session;
        SelectedSession = leftSession;
        SelectedPortItem = AvailablePorts.FirstOrDefault(item =>
            string.Equals(item.PortName, leftSession.PortName, StringComparison.OrdinalIgnoreCase));
        StatusMessage = string.Empty;
        return Task.CompletedTask;
    }

    [RelayCommand]
    private async Task CloseRightPaneAsync(SessionViewModel? session)
    {
        session ??= SelectedRightSession;
        if (session is null)
        {
            return;
        }

        RememberPortOverride(session.PortName);
        CloseFloatSendFor(session.PortName); CloseLogFilterFor(session.PortName);
        RemoveRightSession(session);
        Sessions.Remove(session);
        await session.DisposeAsync();
        NotifyCommandStates();
    }

    /// <summary>
    /// Closes every open session for the plugin apply-and-restart flow: stopping send jobs
    /// and closing serial ports first so the log writer can flush before workers stop.
    /// </summary>
    public async Task CloseAllSessionsForRestartAsync()
    {
        await CommandRunner.StopAsync();
        foreach (SessionViewModel session in Sessions.ToList())
        {
            await CloseSessionAsync(session);
        }

        foreach (SessionViewModel session in RightSessions.ToList())
        {
            await CloseSessionAsync(session);
        }
    }

    /// <summary>Flushes the debounced settings save immediately (restart commit point).</summary>
    public void SaveSettingsNow() => SaveSettings();

    [RelayCommand]
    private async Task CloseSessionAsync(SessionViewModel? session)
    {
        session ??= SelectedSession;
        if (session is null)
        {
            return;
        }

        bool wasSelectedMainSession = ReferenceEquals(SelectedSession, session);
        int sessionIndex = Sessions.IndexOf(session);
        ThemedMessageDialog.CloseOpenFailureFor(session);

        if (session.IsOpen)
        {
            await session.CloseAsync();
        }

        CloseFloatSendFor(session.PortName);
        CloseLogFilterFor(session.PortName);
        if (RightSessions.Contains(session))
        {
            RemoveRightSession(session);
        }

        Sessions.Remove(session);
        await session.DisposeAsync();
        if (wasSelectedMainSession || SelectedSession is null || !Sessions.Contains(SelectedSession))
        {
            List<SessionViewModel> mainSessions = [.. Sessions.Where(candidate => !RightSessions.Contains(candidate))];
            SelectedSession = mainSessions.Count == 0
                ? null
                : mainSessions[Math.Clamp(sessionIndex, 0, mainSessions.Count - 1)];
        }
        if (SelectedSession is not null)
        {
            SelectedPortItem = AvailablePorts.FirstOrDefault(item =>
                string.Equals(item.PortName, SelectedSession.PortName, StringComparison.OrdinalIgnoreCase));
        }
        NotifyCommandStates();
    }

    [RelayCommand]
    private void MoveRightSessionToMain(SessionViewModel? session)
    {
        session ??= SelectedRightSession;
        if (session is null)
        {
            return;
        }

        RemoveRightSession(session);
        SelectedSession = session;
        SelectedPortItem = AvailablePorts.FirstOrDefault(item =>
            string.Equals(item.PortName, session.PortName, StringComparison.OrdinalIgnoreCase));
        NotifyCommandStates();
    }

    [RelayCommand]
    private void SetSplitOrientation(string orientation)
    {
        if (Enum.TryParse(orientation, true, out SplitLayoutOrientation parsed))
        {
            SplitOrientation = parsed;
        }
    }

    internal void MoveSessionTab(string portName, int targetIndex, bool rightPane)
    {
        ObservableCollection<SessionViewModel> collection = rightPane ? RightSessions : Sessions;
        SessionViewModel? session = collection.FirstOrDefault(item =>
            string.Equals(item.PortName, portName, StringComparison.OrdinalIgnoreCase));
        if (session is null)
        {
            return;
        }

        int currentIndex = collection.IndexOf(session);
        int boundedTarget = Math.Clamp(targetIndex, 0, collection.Count - 1);
        if (currentIndex != boundedTarget)
        {
            collection.Move(currentIndex, boundedTarget);
            MarkSettingsDirty();
        }
    }

    [RelayCommand]
    private async Task ToggleSessionConnectionAsync(SessionViewModel session)
    {
        ArgumentNullException.ThrowIfNull(session);
        try
        {
            if (session.IsOpen)
            {
                await session.CloseAsync();
            }
            else
            {
                await session.OpenAsync();
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or TimeoutException or OperationCanceledException)
        {
            StatusMessage = GetResourceString("Status.PortCommandFailed")
                .Replace("{0}", exception.Message, StringComparison.Ordinal);
            Program.DiagnosticLog?.Warning($"Port command failed. Port={session.PortName}.", exception);
        }

        NotifyCommandStates();
    }

    [RelayCommand]
    private async Task SendSessionAsync(SessionViewModel session)
    {
        ArgumentNullException.ThrowIfNull(session);
        try
        {
            await session.SendAsync();
            if (FreezeAfterSend)
            {
                session.FollowEnd = false;
            }

            if (_sendHistory.Record(session.SendText))
            {
                PersistSendHistory();
            }
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException)
        {
            StatusMessage = GetResourceString("Status.InvalidHex").Replace("{0}", exception.Message, StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or TimeoutException or OperationCanceledException)
        {
            StatusMessage = GetResourceString("Status.SendFailed").Replace("{0}", exception.Message, StringComparison.Ordinal);
            Program.DiagnosticLog?.Warning($"Send failed. Port={session.PortName}.", exception);
        }
    }

    private void RemoveRightSession(SessionViewModel session)
    {
        session.IsInRightPane = false;
        RightSessions.Remove(session);
        if (SelectedSession is null || ReferenceEquals(SelectedSession, session))
        {
            SelectedSession = Sessions.FirstOrDefault(item =>
                item.IsOpen && !ReferenceEquals(item, session) && !RightSessions.Contains(item));
        }

        if (SelectedSession is not null)
        {
            SelectedPortItem = AvailablePorts.FirstOrDefault(item =>
                string.Equals(item.PortName, SelectedSession.PortName, StringComparison.OrdinalIgnoreCase));
        }
    }

    private void OnSessionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasSessions));
        MarkSettingsDirty();
    }

    private void OnRightSessionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(IsSplitView));
        if (SelectedRightSession is null || !RightSessions.Contains(SelectedRightSession))
        {
            SelectedRightSession = RightSessions.LastOrDefault();
        }
        MarkSettingsDirty();
    }

    private async Task TogglePortAsync(PortItemViewModel port)
    {
        SelectedPortItem = port;
        SessionViewModel? session = Sessions.FirstOrDefault(
            item => string.Equals(item.PortName, port.PortName, StringComparison.OrdinalIgnoreCase));
        if (session?.IsOpen == true)
        {
            // Toggle the port's own session without pulling a right-pane session into
            // the left workspace, which would sync both panes to one session.
            if (RightSessions.Contains(session))
            {
                SelectedRightSession = session;
            }
            else
            {
                SelectedSession = session;
            }

            string logFilePath = session.WorkspaceSession.CurrentLogFilePath ?? string.Empty;
            await session.CloseAsync();
            Program.DiagnosticLog?.Information(
                $"Close command completed. Port={session.PortName}; CompletedAt={DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}; IsOpen={session.IsOpen}; Fault={session.FaultMessage}; LogFile={logFilePath}");
            NotifyCommandStates();
        }
        else
        {
            await OpenSelectedPortAsync(showFailureDialog: true);
        }
    }

    private async Task<SessionViewModel?> EnsureSessionOpenAsync(string portName)
    {
        SelectedPortItem = AvailablePorts.FirstOrDefault(
            item => string.Equals(item.PortName, portName, StringComparison.OrdinalIgnoreCase));
        SessionViewModel? session = Sessions.FirstOrDefault(
            item => string.Equals(item.PortName, portName, StringComparison.OrdinalIgnoreCase));
        if (session?.IsOpen != true)
        {
            await OpenSelectedPortAsync(showFailureDialog: false);
            session = Sessions.FirstOrDefault(item =>
                string.Equals(item.PortName, portName, StringComparison.OrdinalIgnoreCase));
        }

        return session is { IsOpen: true } ? session : null;
    }

    private void NotifyCommandStates()
    {
        OpenCommand.NotifyCanExecuteChanged();
        CloseCommand.NotifyCanExecuteChanged();
        SendCommand.NotifyCanExecuteChanged();
    }

    internal void OpenSearchFor(SessionViewModel session)
    {
        ArgumentNullException.ThrowIfNull(session);
        ActivateLogSession(session);
        session.Search.OpenCommand.Execute(null);
    }
}
