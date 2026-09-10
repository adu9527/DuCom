namespace DuCom.ViewModels;

public partial class MainViewModel
{
    internal async Task RestorePersistedSessionsAsync()
    {
        if (_sessionsRestored)
        {
            return;
        }

        _sessionsRestored = true;
        Task initialPortRefresh;
        lock (_portRefreshSync)
        {
            initialPortRefresh = _portRefreshTask ?? Task.CompletedTask;
        }
        await initialPortRefresh;
        string[] openPorts = ResolvePersistedOpenSessionPorts();
        if (openPorts.Length == 0)
        {
            return;
        }

        _isLoadingSettings = true;
        try
        {
            foreach (string portName in openPorts)
            {
                if (!AvailablePorts.Any(item => string.Equals(item.PortName, portName, StringComparison.OrdinalIgnoreCase)))
                {
                    Program.DiagnosticLog?.Warning($"Persisted session port is not currently available. Port={portName}");
                    continue;
                }

                SessionViewModel? session = await EnsureSessionOpenAsync(portName);
                if (session is null)
                {
                    Program.DiagnosticLog?.Warning($"Persisted session could not be reopened. Port={portName}");
                }
            }

            HashSet<string> rightPorts = new(_persistedRightPanePorts, StringComparer.OrdinalIgnoreCase);
            SessionViewModel? leftSession = Sessions.FirstOrDefault(session =>
                    session.IsOpen &&
                    !rightPorts.Contains(session.PortName) &&
                    string.Equals(session.PortName, _persistedSelectedSessionPort, StringComparison.OrdinalIgnoreCase))
                ?? Sessions.FirstOrDefault(session => session.IsOpen && !rightPorts.Contains(session.PortName));
            if (leftSession is null)
            {
                leftSession = Sessions.FirstOrDefault(session => session.IsOpen);
                rightPorts.Clear();
            }

            foreach (SessionViewModel session in Sessions.Where(item =>
                         item.IsOpen && rightPorts.Contains(item.PortName) && !ReferenceEquals(item, leftSession)))
            {
                session.IsInRightPane = true;
                if (!RightSessions.Contains(session))
                {
                    RightSessions.Add(session);
                }
            }

            SelectedRightSession = FindOpenSession(_persistedSelectedRightSessionPort, rightPane: true)
                ?? RightSessions.FirstOrDefault(item => item.IsOpen);
            SelectedSession = leftSession;
            if (SelectedSession is not null)
            {
                SelectedPortItem = AvailablePorts.FirstOrDefault(item =>
                    string.Equals(item.PortName, SelectedSession.PortName, StringComparison.OrdinalIgnoreCase));
            }
        }
        finally
        {
            _isLoadingSettings = false;
            NotifyCommandStates();
        }
    }

    private string[] ResolvePersistedOpenSessionPorts()
    {
        IEnumerable<string> ports = _persistedOpenSessionPorts.Count > 0
            ? _persistedOpenSessionPorts
            : _persistedRightPanePorts.Concat(_persistedSessionOrder.Where(portName =>
                !_persistedRightPanePorts.Contains(portName, StringComparer.OrdinalIgnoreCase)).Take(1));
        return [.. ports
            .Where(portName => !string.IsNullOrWhiteSpace(portName))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private SessionViewModel? FindOpenSession(string? portName, bool rightPane)
    {
        if (string.IsNullOrWhiteSpace(portName))
        {
            return null;
        }

        return Sessions.FirstOrDefault(session =>
            session.IsOpen &&
            session.IsInRightPane == rightPane &&
            string.Equals(session.PortName, portName, StringComparison.OrdinalIgnoreCase));
    }
}
