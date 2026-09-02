using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Threading;
using DuCom.Core.Diagnostics;
using DuCom.Core.Storage;
using DuCom.Core.Ports;
using DuCom.Core.Sending;
using DuCom.Core.Telnet;
using DuCom.ViewModels;

namespace DuCom.Services;

/// <summary>
/// Builds and caches immutable session snapshots on the UI thread. Background services
/// (watchdog, variable monitor, Telnet bridge) only ever read the cached immutable arrays
/// and delegate-based probes — they never enumerate the WPF ObservableCollection and never
/// read mutable ViewModel properties from a timer thread. The snapshot is rebuilt whenever
/// a session is added/removed or its open state changes.
/// </summary>
public sealed class SessionProbeProvider : IDisposable
{
    private readonly ObservableCollection<SessionViewModel> _sessions;
    private readonly object _gate = new();
    private IReadOnlyList<WatchdogSessionProbe> _watchdogProbes = [];
    private IReadOnlyList<VariableMonitorSessionProbe> _monitorProbes = [];
    private IReadOnlyList<TelnetSessionProbe> _telnetProbes = [];
    private IReadOnlyList<CommandSessionProbe> _commandProbes = [];
    private bool _disposed;

    public SessionProbeProvider(ObservableCollection<SessionViewModel> sessions)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _sessions.CollectionChanged += OnSessionsChanged;
        Rebuild();
    }

    /// <summary>Immutable watchdog view of the current sessions (safe from any thread).</summary>
    public IReadOnlyList<WatchdogSessionProbe> WatchdogSnapshot
    {
        get
        {
            lock (_gate)
            {
                return _watchdogProbes;
            }
        }
    }

    /// <summary>Immutable variable-monitor view of the current sessions (safe from any thread).</summary>
    public IReadOnlyList<VariableMonitorSessionProbe> MonitorSnapshot
    {
        get
        {
            lock (_gate)
            {
                return _monitorProbes;
            }
        }
    }

    public IReadOnlyList<CommandSessionProbe> CommandSnapshot
    {
        get
        {
            lock (_gate)
            {
                return _commandProbes;
            }
        }
    }

    /// <summary>Finds the open-session send/pull probe for a port, or null when closed/absent.</summary>
    public TelnetSessionProbe? FindTelnetProbe(string? portName)
    {
        if (string.IsNullOrWhiteSpace(portName))
        {
            return null;
        }

        IReadOnlyList<TelnetSessionProbe> probes;
        lock (_gate)
        {
            probes = _telnetProbes;
        }

        return probes.FirstOrDefault(probe =>
            string.Equals(probe.PortName, portName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Finds the ViewModel for a port. The returned reference may only be touched on the UI
    /// thread; background callers must dispatch before using it.
    /// </summary>
    public SessionViewModel? FindViewModel(string portName)
    {
        SessionViewModel? session = _sessions.FirstOrDefault(candidate =>
            string.Equals(candidate.PortName, portName, StringComparison.OrdinalIgnoreCase));
        return session is { IsOpen: true } ? session : null;
    }

    private void OnSessionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (SessionViewModel session in e.OldItems.OfType<SessionViewModel>())
            {
                session.PropertyChanged -= OnSessionPropertyChanged;
            }
        }

        if (e.NewItems is not null)
        {
            foreach (SessionViewModel session in e.NewItems.OfType<SessionViewModel>())
            {
                session.PropertyChanged += OnSessionPropertyChanged;
            }
        }

        Rebuild();
    }

    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SessionViewModel.IsOpen))
        {
            Rebuild();
        }
    }

    /// <summary>Rebuilds the immutable snapshots. CollectionChanged and IsOpen changes arrive on the UI thread.</summary>
    private void Rebuild()
    {
        Dispatcher? dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(Rebuild);
            return;
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            List<WatchdogSessionProbe> watchdog = [];
            List<VariableMonitorSessionProbe> monitor = [];
            List<TelnetSessionProbe> telnet = [];
            List<CommandSessionProbe> commands = [];
            foreach (SessionViewModel session in _sessions)
            {
                if (!session.IsOpen)
                {
                    continue;
                }

                SessionViewModel captured = session;
                Func<LineCursor?, LineStoreSnapshot> pull = cursor => captured.WorkspaceSession.GetDisplaySnapshot(cursor, 2_048);
                watchdog.Add(new WatchdogSessionProbe(captured.PortName, IsOpen: true, pull));
                monitor.Add(new VariableMonitorSessionProbe(captured.PortName, IsOpen: true, pull));
                telnet.Add(new TelnetSessionProbe(
                    captured.PortName,
                    pull,
                    (command, cancellationToken) => captured.SendRawCommandAsync(command, cancellationToken)));
                commands.Add(new CommandSessionProbe(captured.PortName, captured.WorkspaceSession));
            }

            _watchdogProbes = watchdog;
            _monitorProbes = monitor;
            _telnetProbes = telnet;
            _commandProbes = commands;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _sessions.CollectionChanged -= OnSessionsChanged;
        foreach (SessionViewModel session in _sessions)
        {
            session.PropertyChanged -= OnSessionPropertyChanged;
        }
    }
}

public sealed record CommandSessionProbe(string PortName, IWorkspaceSession Session);
