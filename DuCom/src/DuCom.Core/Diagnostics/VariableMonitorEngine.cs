using System.Diagnostics;
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
    Func<LineCursor?, LineStoreSnapshot> PullLines)
{
    public VariableMonitorSessionProbe(
        string portName,
        bool isOpen,
        string? runtimeId,
        Func<LineCursor?, LineStoreSnapshot> pullLines)
        : this(portName, isOpen, pullLines)
    {
        RuntimeId = runtimeId;
    }

    public VariableMonitorSessionProbe(
        string portName,
        bool isOpen,
        string? runtimeId,
        Func<LineCursor?, int, LineStoreSnapshot> pullLinesPage)
        : this(portName, isOpen, cursor => pullLinesPage(cursor, int.MaxValue))
    {
        RuntimeId = runtimeId;
        PullLinesPage = pullLinesPage;
    }

    public string? RuntimeId { get; init; }

    public Func<LineCursor?, int, LineStoreSnapshot>? PullLinesPage { get; init; }
}

/// <summary>
/// Pure per-tick variable-monitor orchestration: one context per open session, incremental
/// cursor pulls, ANSI projection, and rule evaluation through the shared
/// <see cref="VariableMonitorEvaluator"/>. No I/O, no dispatch.
/// </summary>
public sealed class VariableMonitorEngine
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Context> _contexts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, VariableSeriesBuffer> _series = [];
    private readonly Dictionary<Guid, SamplingAccumulator> _sampling = [];
    private readonly List<VariableMonitorGap> _gaps = [];
    private readonly VariableMonitorEngineOptions _options;
    private VariableMonitorEvaluator _evaluator = new();
    private IReadOnlyList<VariableMonitorRule> _rules = [];
    private long _generation;

    public VariableMonitorEngine(VariableMonitorEngineOptions? options = null)
    {
        _options = options ?? new VariableMonitorEngineOptions();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.MaximumPointsPerSeries);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.MaximumPagesPerTick);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.MaximumItemsPerTick);
        if (_options.EffectiveMaximumWorkTimePerTick <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
    }

    public void UpdateRules(IReadOnlyList<VariableMonitorRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        lock (_gate)
        {
            Dictionary<Guid, VariableMonitorRule> previous = _rules.ToDictionary(rule => rule.Id);
            _rules = rules;
            _evaluator.UpdateRules(rules);

            bool cleared = false;
            foreach (Guid id in _series.Keys.Where(id => rules.All(rule => rule.Id != id)).ToList())
            {
                _series.Remove(id);
                _sampling.Remove(id);
                cleared = true;
            }

            foreach (VariableMonitorRule rule in rules)
            {
                if (previous.TryGetValue(rule.Id, out VariableMonitorRule? oldRule) &&
                    VariableMonitorEvaluator.HasExtractionSemanticsChanged(oldRule, rule))
                {
                    _series.Remove(rule.Id);
                    _sampling.Remove(rule.Id);
                    cleared = true;
                }
            }

            if (cleared)
            {
                _generation++;
            }
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

    public VariablePlotSnapshot GetPlotSnapshot()
    {
        lock (_gate)
        {
            List<VariableSeriesSnapshot> series = [];
            foreach (VariableMonitorRule rule in _rules.Where(rule => rule.PlotEnabled).OrderBy(rule => rule.Order))
            {
                if (_series.TryGetValue(rule.Id, out VariableSeriesBuffer? buffer))
                {
                    series.Add(new VariableSeriesSnapshot(rule, buffer.Snapshot(), buffer.EvictedSampleCount));
                }
                else
                {
                    series.Add(new VariableSeriesSnapshot(rule, Array.Empty<VariableNumericSample>(), 0));
                }
            }

            return new VariablePlotSnapshot(
                _generation,
                Array.AsReadOnly(series.ToArray()),
                Array.AsReadOnly(_gaps.ToArray()),
                Array.AsReadOnly(_evaluator.RuleStatuses.ToArray()));
        }
    }

    public void ClearHistory()
    {
        lock (_gate)
        {
            _series.Clear();
            _sampling.Clear();
            _gaps.Clear();
            _evaluator.ClearSamples();
            _generation++;
        }
    }

    /// <summary>Runs one sampling pass over the currently open sessions.</summary>
    public void Tick(IReadOnlyList<VariableMonitorSessionProbe> sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        lock (_gate)
        {
            SynchronizeContexts(sessions);
            Stopwatch stopwatch = Stopwatch.StartNew();
            int remainingItems = _options.MaximumItemsPerTick;
            foreach (Context context in _contexts.Values)
            {
                PullNewLines(context, stopwatch, ref remainingItems);
                if (remainingItems == 0 || stopwatch.Elapsed >= _options.EffectiveMaximumWorkTimePerTick)
                {
                    break;
                }
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
            if (!_contexts.TryGetValue(session.PortName, out Context? context) ||
                !string.Equals(context.RuntimeId, session.RuntimeId, StringComparison.Ordinal))
            {
                LineCursor? cursor = WatchdogEngine.GetEndCursor(session.PullLines(null));
                _contexts[session.PortName] = new Context(
                    session.PortName,
                    session.RuntimeId,
                    session.PullLines,
                    session.PullLinesPage,
                    cursor);
            }
            else
            {
                context.PullLines = session.PullLines;
                context.PullLinesPage = session.PullLinesPage;
            }
        }

        foreach (string port in _contexts.Keys.Where(port => !openPorts.Contains(port)).ToList())
        {
            _contexts.Remove(port);
        }
    }

    private void PullNewLines(Context context, Stopwatch stopwatch, ref int remainingItems)
    {
        for (int page = 0; page < _options.MaximumPagesPerTick && remainingItems > 0; page++)
        {
            if (stopwatch.Elapsed >= _options.EffectiveMaximumWorkTimePerTick)
            {
                return;
            }

            int requested = remainingItems;
            LineStoreSnapshot snapshot = context.PullLinesPage is { } pullPage
                ? pullPage(context.Cursor, requested)
                : context.PullLines(context.Cursor);

            if (context.Cursor is LineCursor cursor &&
                snapshot.FirstLogicalId is long firstId && cursor.LogicalId < firstId)
            {
                long estimated = Math.Max(1, firstId - cursor.LogicalId - 1);
                _gaps.Add(new VariableMonitorGap(context.PortName, DateTimeOffset.UtcNow, estimated, "Cursor fell behind retained display lines."));
            }

            if (snapshot.Lines.Count == 0)
            {
                if (snapshot.FirstLogicalId is long first &&
                    (context.Cursor is null || context.Cursor.Value.LogicalId < first))
                {
                    context.Cursor = WatchdogEngine.GetEndCursor(snapshot);
                }

                return;
            }

            int processed = 0;
            foreach (StoredLine line in snapshot.Lines)
            {
                if (remainingItems == 0 || stopwatch.Elapsed >= _options.EffectiveMaximumWorkTimePerTick)
                {
                    break;
                }

                string clean = context.Projector.Project(line.Text, null).DisplayText;
                foreach (VariableNumericSample sample in _evaluator.AppendLine(context.PortName, clean, line.TimestampUtc))
                {
                    AddNumericSample(sample);
                }

                context.Cursor = new LineCursor(line.LogicalId, line.SegmentIndex);
                remainingItems--;
                processed++;
            }

            if (processed < snapshot.Lines.Count)
            {
                return;
            }
        }
    }

    private void AddNumericSample(VariableNumericSample sample)
    {
        VariableMonitorRule? rule = _rules.FirstOrDefault(candidate => candidate.Id == sample.RuleId);
        if (rule is null || !rule.PlotEnabled)
        {
            return;
        }

        if (rule.SamplingMode == VariableMonitorSamplingMode.EveryMatch || rule.SampleIntervalMs <= 0)
        {
            AddToSeries(sample);
            return;
        }

        long intervalTicks = TimeSpan.FromMilliseconds(rule.SampleIntervalMs).Ticks;
        long bucket = sample.SampledAtUtc.UtcTicks / intervalTicks;
        if (!_sampling.TryGetValue(rule.Id, out SamplingAccumulator? accumulator))
        {
            _sampling[rule.Id] = new SamplingAccumulator(bucket, sample);
            return;
        }

        if (bucket == accumulator.Bucket)
        {
            accumulator.Add(sample);
            return;
        }

        if (bucket < accumulator.Bucket)
        {
            AddToSeries(sample);
            return;
        }

        foreach (VariableNumericSample completed in accumulator.Complete(rule.SamplingMode))
        {
            AddToSeries(completed);
        }

        _sampling[rule.Id] = new SamplingAccumulator(bucket, sample);
    }

    private void AddToSeries(VariableNumericSample sample)
    {
        if (!_series.TryGetValue(sample.RuleId, out VariableSeriesBuffer? buffer))
        {
            buffer = new VariableSeriesBuffer(_options.MaximumPointsPerSeries, _options.EffectiveRetention);
            _series[sample.RuleId] = buffer;
        }

        buffer.Add(sample);
    }

    private sealed class Context(
        string portName,
        string? runtimeId,
        Func<LineCursor?, LineStoreSnapshot> pullLines,
        Func<LineCursor?, int, LineStoreSnapshot>? pullLinesPage,
        LineCursor? cursor)
    {
        public string PortName { get; } = portName;

        public string? RuntimeId { get; } = runtimeId;

        public Func<LineCursor?, LineStoreSnapshot> PullLines { get; set; } = pullLines;

        public Func<LineCursor?, int, LineStoreSnapshot>? PullLinesPage { get; set; } = pullLinesPage;

        public LineCursor? Cursor { get; set; } = cursor;

        public AnsiDisplayProjector Projector { get; } = new();
    }

    private sealed class SamplingAccumulator(long bucket, VariableNumericSample first)
    {
        private VariableNumericSample _latest = first;
        private VariableNumericSample _minimum = first;
        private VariableNumericSample _maximum = first;
        private double _sum = first.NumericValue;
        private int _count = 1;

        public long Bucket { get; } = bucket;

        public void Add(VariableNumericSample sample)
        {
            _latest = sample;
            _sum += sample.NumericValue;
            _count++;
            if (sample.NumericValue < _minimum.NumericValue) _minimum = sample;
            if (sample.NumericValue > _maximum.NumericValue) _maximum = sample;
        }

        public IReadOnlyList<VariableNumericSample> Complete(VariableMonitorSamplingMode mode) => mode switch
        {
            VariableMonitorSamplingMode.Latest => [_latest with { SourceSampleCount = _count }],
            VariableMonitorSamplingMode.Average => [_latest with { NumericValue = _sum / _count, SourceSampleCount = _count }],
            VariableMonitorSamplingMode.MinMax when _minimum.SampledAtUtc <= _maximum.SampledAtUtc =>
                _minimum == _maximum
                    ? [_minimum with { SourceSampleCount = _count }]
                    : [_minimum with { SourceSampleCount = _count }, _maximum with { SourceSampleCount = _count }],
            VariableMonitorSamplingMode.MinMax =>
                [_maximum with { SourceSampleCount = _count }, _minimum with { SourceSampleCount = _count }],
            _ => [_latest],
        };
    }
}
