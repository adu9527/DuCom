using System.Diagnostics;
using System.Windows.Media;
using DuCom.Services;

namespace DuCom.ViewModels;

public partial class MainViewModel
{
    private void OnCompositionRendering(object? sender, EventArgs e)
    {
        if (e is not RenderingEventArgs { RenderingTime: TimeSpan renderingTime })
        {
            return;
        }

        FramePacerDecision decision = FramePacer.Advance(renderingTime, _framePacerState);
        _framePacerState = decision.State;
        if (!decision.ShouldRender)
        {
            return;
        }

        OnRenderTick(decision.ShouldRefreshStatus);
    }

    private void OnRenderTick(bool shouldRefreshStatus)
    {
        HashSet<SessionViewModel> sessionsToProject = [];
        if (Workspace.SelectedSession is not null)
        {
            sessionsToProject.Add(Workspace.SelectedSession);
        }

        if (Workspace.SelectedRightSession is not null)
        {
            sessionsToProject.Add(Workspace.SelectedRightSession);
        }

        bool commandStateChanged = false;
        TimeSpan projectionBudget = FramePacer.GetProjectionBudget(sessionsToProject.Count);
        foreach (SessionViewModel session in sessionsToProject)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            commandStateChanged |= session.PullDisplaySnapshot(publishSearchSnapshot: false, projectionBudget: projectionBudget);
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
        if (!shouldRefreshStatus)
        {
            if (commandStateChanged)
            {
                NotifyCommandStates();
            }
            return;
        }

        Dictionary<string, SessionViewModel> sessionsByPort = Workspace.Sessions
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
