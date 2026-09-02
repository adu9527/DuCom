using DuCom.Core.Ports;
using DuCom.Core.Sending;
using DuCom.Core.Storage;

namespace DuCom.Services;

/// <summary>
/// Application adapter over <see cref="ScriptGroupHost"/>: supplies session-bound send/probe
/// delegates through existing workspace boundaries (Send/GetDisplaySnapshot) without adding a
/// receive/log consumer. Lifecycle ownership belongs to <see cref="ViewModels.MainViewModel"/>;
/// tool windows may only observe <see cref="StateChanged"/> and must never dispose this host.
/// </summary>
public sealed class CommandGroupRunnerHost : IAsyncDisposable
{
    private const int ProbeTailCharacters = ReceiveTail.DefaultMaxLength;

    private readonly Func<IReadOnlyList<string>> _selectedPortNamesProvider;
    private readonly Func<IReadOnlyList<CommandSessionProbe>> _sessionProvider;
    private readonly Action<string> _statusReporter;
    private readonly ScriptGroupHost _host;
    private readonly object _probeGate = new();
    private readonly Dictionary<string, ProbeState> _probes = new(StringComparer.OrdinalIgnoreCase);

    public CommandGroupRunnerHost(
        Func<IReadOnlyList<string>> selectedPortNamesProvider,
        Func<IReadOnlyList<CommandSessionProbe>> sessionProvider,
        Action<string> statusReporter)
    {
        _selectedPortNamesProvider = selectedPortNamesProvider ?? throw new ArgumentNullException(nameof(selectedPortNamesProvider));
        _sessionProvider = sessionProvider ?? throw new ArgumentNullException(nameof(sessionProvider));
        _statusReporter = statusReporter ?? throw new ArgumentNullException(nameof(statusReporter));
        MultiTargetCommandScriptRunner runner = new(ResolveTargets);
        _host = new ScriptGroupHost(
            canStart: HasSelection,
            runner,
            statusReporter,
            errorLogger: exception => Program.DiagnosticLog?.Error("Command group run failed.", exception),
            startRejectedStatusKey: "Status.CommandRunNoSelection");
    }

    public bool IsRunning => _host.IsRunning;

    public CommandGroup? RunningGroup => _host.RunningGroup;

    public event EventHandler? StateChanged
    {
        add => _host.StateChanged += value;
        remove => _host.StateChanged -= value;
    }

    public event EventHandler<ScriptCommandStatusEventArgs>? CommandStatusChanged
    {
        add => _host.CommandStatusChanged += value;
        remove => _host.CommandStatusChanged -= value;
    }

    private bool HasSelection()
    {
        if (_selectedPortNamesProvider().Count == 0)
        {
            return false;
        }

        return true;
    }

    private List<ScriptCommandTarget> ResolveTargets()
    {
        HashSet<string> selected = new(_selectedPortNamesProvider(), StringComparer.OrdinalIgnoreCase);
        List<ScriptCommandTarget> targets = [];
        foreach (CommandSessionProbe probe in _sessionProvider()
            .Where(probe => selected.Contains(probe.PortName))
            .OrderBy(probe => probe.PortName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(probe => probe.PortName, StringComparer.Ordinal))
        {
            ProbeState state;
            lock (_probeGate)
            {
                if (!_probes.TryGetValue(probe.PortName, out state!))
                {
                    state = new ProbeState();
                    _probes.Add(probe.PortName, state);
                }

                state.Cursor = AdvanceCursorToNow(probe.Session);
                state.Tail = string.Empty;
                state.LastLogicalId = null;
            }

            CommandSessionProbe captured = probe;
            targets.Add(new ScriptCommandTarget(
                captured.PortName,
                async (command, cancellationToken) =>
                {
                    state.Cursor = AdvanceCursorToNow(captured.Session);
                    state.Tail = string.Empty;
                    state.LastLogicalId = null;
                    await captured.Session.SendAsync(
                        command.IsHex ? SendMode.Hex : SendMode.Str,
                        command.Payload,
                        command.Newline,
                        cancellationToken);
                },
                (command, cancellationToken) => ProbeForExpectedResult(captured, state, command, cancellationToken)));
        }

        return targets;
    }

    private static Task<bool> ProbeForExpectedResult(
        CommandSessionProbe probe,
        ProbeState state,
        ScriptCommand command,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LineStoreSnapshot snapshot = probe.Session.GetDisplaySnapshot(state.Cursor, maximumSegments: 256);
        foreach (StoredLine line in snapshot.Lines)
        {
            if (line.Direction != LineDirection.Rx)
            {
                continue;
            }

            // Continuation pieces of one soft-wrapped logical line must stay contiguous so an
            // expected reply can span several stored segments.
            string separator = line.LogicalId == state.LastLogicalId
                ? ReceiveTail.ContinuationSeparator
                : ReceiveTail.LineSeparator;
            state.Tail = ReceiveTail.Append(state.Tail, line.Text, ProbeTailCharacters, separator);
            state.LastLogicalId = line.LogicalId;
        }

        if (snapshot.Lines.Count > 0)
        {
            StoredLine last = snapshot.Lines[^1];
            state.Cursor = new LineCursor(last.LogicalId, last.SegmentIndex);
        }

        return Task.FromResult(state.Tail.Contains(command.ExpectedResult, StringComparison.Ordinal));
    }

    /// <summary>Advances a cursor to the newest stored segment so result checks only see fresh data.</summary>
    private static LineCursor? AdvanceCursorToNow(IWorkspaceSession session)
    {
        LineCursor? cursor = null;
        while (true)
        {
            LineStoreSnapshot snapshot = session.GetDisplaySnapshot(cursor, maximumSegments: 1_024);
            if (snapshot.Lines.Count == 0)
            {
                break;
            }

            StoredLine last = snapshot.Lines[^1];
            cursor = new LineCursor(last.LogicalId, last.SegmentIndex);
            if (snapshot.Lines.Count < 1_024)
            {
                break;
            }
        }

        return cursor;
    }

    public bool Start(CommandGroup group) => _host.Start(group);

    public Task StopAsync() => _host.StopAsync();

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private sealed class ProbeState
    {
        public LineCursor? Cursor { get; set; }

        public string Tail { get; set; } = string.Empty;

        public long? LastLogicalId { get; set; }
    }
}
