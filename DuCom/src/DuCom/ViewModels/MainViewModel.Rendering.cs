using System.Diagnostics;
using System.Windows;
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

        if (_disposed || Workspace.Sessions.Count == 0) return;

        // Tool windows share this dispatcher. While the operator works in a tool
        // (including a modal editor owned by an independent production window),
        // keep background receive/logging but defer the inactive main log's repaint.
        // Connection and fault polling keeps its normal status cadence.
        Window? main = Application.Current?.MainWindow;
        bool mainShowable = main is null ||
            main.IsEnabled && main.IsVisible && main.WindowState != WindowState.Minimized;
        // Defer the repaint only while another window of THIS application holds the
        // activation. When a foreign application is active (e.g. the mouse moved to
        // the other monitor and another program took focus), the main log must keep
        // flowing; otherwise the display looks paused on dual-monitor setups.
        bool projectDisplay = mainShowable &&
            (main is null || main.IsActive || !IsAnyApplicationWindowActive());
        if (!projectDisplay && renderingTime - _framePacerState.LastStatusRefreshTime < FramePacer.StatusRefreshInterval)
            return;

        FramePacerDecision decision = FramePacer.Advance(renderingTime, _framePacerState);
        _framePacerState = decision.State;
        if (!decision.ShouldRender)
        {
            return;
        }

        OnRenderTick(decision.ShouldRefreshStatus, projectDisplay);
    }

    internal static bool RefreshSessionDisplay(SessionViewModel session, bool projectDisplay, TimeSpan projectionBudget) =>
        projectDisplay ? session.PullDisplaySnapshot(publishSearchSnapshot: false, projectionBudget: projectionBudget)
            : session.RefreshStatus();

    private static bool IsAnyApplicationWindowActive()
    {
        WindowCollection? windows = Application.Current?.Windows;
        if (windows is null)
        {
            return false;
        }

        for (int i = 0; i < windows.Count; i++)
        {
            if (windows[i] is { IsActive: true })
            {
                return true;
            }
        }

        return false;
    }

    private void OnRenderTick(bool shouldRefreshStatus, bool projectDisplay = true)
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
        if (shouldRefreshStatus && !projectDisplay)
        {
            // Nonselected sessions also need fault/reconnect state while a tool is active.
            foreach (SessionViewModel session in Workspace.Sessions)
                if (!sessionsToProject.Contains(session)) commandStateChanged |= session.RefreshStatus();
        }
        TimeSpan projectionBudget = FramePacer.GetProjectionBudget(sessionsToProject.Count);
        foreach (SessionViewModel session in sessionsToProject)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            commandStateChanged |= RefreshSessionDisplay(session, projectDisplay, projectionBudget);
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
