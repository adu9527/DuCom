using DuCom.Core.Parsing;
using DuCom.Core.Storage;

namespace DuCom.Core.Diagnostics;

/// <summary>
/// Immutable, delegate-only view of one serial session consumed by the watchdog. The
/// application layer builds these on the UI thread; the engine never reads a ViewModel
/// property from a background thread.
/// </summary>
public sealed record WatchdogSessionProbe(
    string PortName,
    bool IsOpen,
    Func<LineCursor?, LineStoreSnapshot> PullLines);

/// <summary>One fired rule plus the port it fired for, returned by a watchdog tick.</summary>
public sealed record WatchdogTrigger(string PortName, WatchdogFiredRule Fired);

/// <summary>
/// Pure per-tick watchdog orchestration: keeps one evaluation context per open session
/// (cursor + evaluator + ANSI projector), pulls new display lines through the probe's
/// snapshot delegate, and returns the rules that fired. Sessions that close have their
/// context dropped; sessions that appear get a fresh context anchored at their current
/// end. Actions are executed by the caller; this type performs no I/O and no dispatch.
/// </summary>
public sealed class WatchdogEngine
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Context> _contexts = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<WatchdogRule> _rules = [];

    public int ActiveContextCount
    {
        get
        {
            lock (_gate)
            {
                return _contexts.Count;
            }
        }
    }

    public bool HasRegexTimedOut
    {
        get
        {
            lock (_gate)
            {
                return _contexts.Values.Any(context => context.Evaluator.HasRegexTimedOut);
            }
        }
    }

    public void UpdateRules(IReadOnlyList<WatchdogRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        lock (_gate)
        {
            _rules = rules;
            foreach (Context context in _contexts.Values)
            {
                context.Evaluator.UpdateRules(rules);
            }
        }
    }

    /// <summary>Runs one evaluation pass over the currently open sessions.</summary>
    public IReadOnlyList<WatchdogTrigger> Tick(IReadOnlyList<WatchdogSessionProbe> sessions, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        List<WatchdogTrigger> triggers = [];
        lock (_gate)
        {
            SynchronizeContexts(sessions, nowUtc);
            foreach (Context context in _contexts.Values)
            {
                PullNewLines(context);
                foreach (WatchdogFiredRule fired in context.Evaluator.Check(nowUtc))
                {
                    triggers.Add(new WatchdogTrigger(context.PortName, fired));
                }
            }
        }

        return triggers;
    }

    private void SynchronizeContexts(IReadOnlyList<WatchdogSessionProbe> sessions, DateTimeOffset nowUtc)
    {
        HashSet<string> openPorts = new(StringComparer.OrdinalIgnoreCase);
        foreach (WatchdogSessionProbe session in sessions)
        {
            if (!session.IsOpen)
            {
                continue;
            }

            openPorts.Add(session.PortName);
            if (_contexts.TryGetValue(session.PortName, out Context? context))
            {
                context.PullLines = session.PullLines;
            }
            else
            {
                WatchdogEvaluator evaluator = new();
                evaluator.UpdateRules(_rules);
                LineCursor? cursor = GetEndCursor(session.PullLines(null));
                _contexts[session.PortName] = new Context(session.PortName, session.PullLines, evaluator, cursor);
                evaluator.Start(nowUtc);
            }
        }

        foreach (string port in _contexts.Keys.Where(port => !openPorts.Contains(port)).ToList())
        {
            _contexts.Remove(port);
        }
    }

    private static void PullNewLines(Context context)
    {
        LineStoreSnapshot snapshot = context.PullLines(context.Cursor);
        if (snapshot.Lines.Count > 0)
        {
            foreach (StoredLine line in snapshot.Lines)
            {
                string clean = context.Projector.Project(line.Text, null).DisplayText;
                context.Evaluator.AppendLine(clean, line.TimestampUtc);
            }

            StoredLine last = snapshot.Lines[^1];
            context.Cursor = new LineCursor(last.LogicalId, last.SegmentIndex);
        }

        if (snapshot.FirstLogicalId is not null &&
            (context.Cursor is null || context.Cursor.Value.LogicalId < snapshot.FirstLogicalId.Value))
        {
            // Store was cleared or evicted past the cursor: future lines will simply not
            // match old content; the watchdog continues from the current end.
            context.Cursor = GetEndCursor(snapshot);
        }
    }

    internal static LineCursor? GetEndCursor(LineStoreSnapshot snapshot) =>
        snapshot.LastLogicalId is long lastId
            ? new LineCursor(lastId, snapshot.Lines.Count > 0 ? snapshot.Lines[^1].SegmentIndex : 0)
            : null;

    private sealed class Context(string portName, Func<LineCursor?, LineStoreSnapshot> pullLines, WatchdogEvaluator evaluator, LineCursor? cursor)
    {
        public string PortName { get; } = portName;

        public Func<LineCursor?, LineStoreSnapshot> PullLines { get; set; } = pullLines;

        public WatchdogEvaluator Evaluator { get; } = evaluator;

        public LineCursor? Cursor { get; set; } = cursor;

        public AnsiDisplayProjector Projector { get; } = new();
    }
}
