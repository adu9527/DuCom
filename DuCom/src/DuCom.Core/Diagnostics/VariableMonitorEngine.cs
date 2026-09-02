using DuCom.Core.Parsing;
using DuCom.Core.Storage;

namespace DuCom.Core.Diagnostics;

/// <summary>
/// Immutable, delegate-only view of one serial session consumed by the variable monitor.
/// Built on the UI thread by the application layer.
/// </summary>
public sealed record VariableMonitorSessionProbe(
    string PortName,
    bool IsOpen,
    Func<LineCursor?, LineStoreSnapshot> PullLines);

/// <summary>
/// Pure per-tick variable-monitor orchestration: one context per open session, incremental
/// cursor pulls, ANSI projection, and rule evaluation through the shared
/// <see cref="VariableMonitorEvaluator"/>. No I/O, no dispatch.
/// </summary>
public sealed class VariableMonitorEngine
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Context> _contexts = new(StringComparer.OrdinalIgnoreCase);
    private VariableMonitorEvaluator _evaluator = new();
    private IReadOnlyList<VariableMonitorRule> _rules = [];

    public void UpdateRules(IReadOnlyList<VariableMonitorRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        lock (_gate)
        {
            _rules = rules;
            VariableMonitorEvaluator evaluator = new();
            evaluator.UpdateRules(rules);
            _evaluator = evaluator;
        }
    }

    public bool IsEmpty
    {
        get
        {
            lock (_gate)
            {
                return _rules.Count == 0;
            }
        }
    }

    public IReadOnlyList<VariableMonitorRule> Rules
    {
        get
        {
            lock (_gate)
            {
                return _rules;
            }
        }
    }

    public IReadOnlyList<(VariableMonitorRule Rule, VariableMonitorSample? Sample)> GetRuleStates()
    {
        lock (_gate)
        {
            Dictionary<Guid, VariableMonitorSample> samples = [];
            foreach (VariableMonitorSample sample in _evaluator.AllSamples())
            {
                samples[sample.RuleId] = sample;
            }

            return [.. _rules
                .OrderBy(rule => rule.Order)
                .Select(rule => (rule, samples.TryGetValue(rule.Id, out VariableMonitorSample? sample) ? sample : null))];
        }
    }

    /// <summary>Runs one sampling pass over the currently open sessions.</summary>
    public void Tick(IReadOnlyList<VariableMonitorSessionProbe> sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        lock (_gate)
        {
            SynchronizeContexts(sessions);
            foreach (Context context in _contexts.Values)
            {
                PullNewLines(context);
            }
        }
    }

    private void SynchronizeContexts(IReadOnlyList<VariableMonitorSessionProbe> sessions)
    {
        HashSet<string> openPorts = new(StringComparer.OrdinalIgnoreCase);
        foreach (VariableMonitorSessionProbe session in sessions)
        {
            if (!session.IsOpen)
            {
                continue;
            }

            openPorts.Add(session.PortName);
            if (!_contexts.TryGetValue(session.PortName, out Context? context))
            {
                LineCursor? cursor = WatchdogEngine.GetEndCursor(session.PullLines(null));
                _contexts[session.PortName] = new Context(session.PortName, session.PullLines, cursor);
            }
            else
            {
                context.PullLines = session.PullLines;
            }
        }

        foreach (string port in _contexts.Keys.Where(port => !openPorts.Contains(port)).ToList())
        {
            _contexts.Remove(port);
        }
    }

    private void PullNewLines(Context context)
    {
        LineStoreSnapshot snapshot = context.PullLines(context.Cursor);
        if (snapshot.Lines.Count > 0)
        {
            foreach (StoredLine line in snapshot.Lines)
            {
                string clean = context.Projector.Project(line.Text, null).DisplayText;
                _evaluator.AppendLine(context.PortName, clean, line.TimestampUtc);
            }

            StoredLine last = snapshot.Lines[^1];
            context.Cursor = new LineCursor(last.LogicalId, last.SegmentIndex);
        }
    }

    private sealed class Context(string portName, Func<LineCursor?, LineStoreSnapshot> pullLines, LineCursor? cursor)
    {
        public string PortName { get; } = portName;

        public Func<LineCursor?, LineStoreSnapshot> PullLines { get; set; } = pullLines;

        public LineCursor? Cursor { get; set; } = cursor;

        public AnsiDisplayProjector Projector { get; } = new();
    }
}
