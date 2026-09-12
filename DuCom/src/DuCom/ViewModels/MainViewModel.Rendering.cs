using System.Diagnostics;
using System.Windows.Media;

namespace DuCom.ViewModels;

public partial class MainViewModel
{
    private void OnCompositionRendering(object? sender, EventArgs e)
    {
        if (e is not RenderingEventArgs { RenderingTime: TimeSpan renderingTime } ||
            renderingTime < _nextRenderTime)
        {
            return;
        }

        _lastRenderTime = renderingTime;
        _nextRenderTime += MinimumRenderInterval;
        if (_nextRenderTime <= renderingTime)
        {
            _nextRenderTime = renderingTime + MinimumRenderInterval;
        }
        OnRenderTick(renderingTime);
    }

    private void OnRenderTick(TimeSpan renderingTime)
    {
        HashSet<SessionViewModel> sessionsToProject = [];
        if (SelectedSession is not null)
        {
            sessionsToProject.Add(SelectedSession);
        }

        if (SelectedRightSession is not null)
        {
            sessionsToProject.Add(SelectedRightSession);
        }

        bool commandStateChanged = false;
        foreach (SessionViewModel session in sessionsToProject)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            commandStateChanged |= session.PullDisplaySnapshot(session.Search.IsOpen);
            stopwatch.Stop();
            if (stopwatch.Elapsed >= SlowOperationThreshold)
            {
                long now = Stopwatch.GetTimestamp();
                if (now >= _nextSlowProjectionLogTimestamp)
                {
                    _nextSlowProjectionLogTimestamp = now + Stopwatch.Frequency;
                    Program.DiagnosticLog?.Warning(
                        $"Slow UI snapshot projection. Port={session.PortName}; ElapsedMs={stopwatch.Elapsed.TotalMilliseconds:0.0}; VisibleLines={session.VisibleLines.Count}");
                }
            }
        }
        if (renderingTime - _lastStatusRefreshTime < StatusRefreshInterval)
        {
            if (commandStateChanged)
            {
                NotifyCommandStates();
            }
            return;
        }

        _lastStatusRefreshTime = renderingTime;
        Dictionary<string, SessionViewModel> sessionsByPort = Sessions
            .GroupBy(session => session.PortName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        foreach (PortItemViewModel port in AvailablePorts)
        {
            sessionsByPort.TryGetValue(port.PortName, out SessionViewModel? session);
            port.Update(session);
        }
        _serialParametersWindow?.RefreshTransportState();

        if (commandStateChanged)
        {
            NotifyCommandStates();
        }
    }
}
