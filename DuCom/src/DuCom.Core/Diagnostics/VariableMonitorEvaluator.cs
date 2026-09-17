using System.Globalization;
using System.Text.RegularExpressions;

namespace DuCom.Core.Diagnostics;

public enum VariableMonitorValueType
{
    Number,
    Text,
}

public enum VariableMonitorSamplingMode
{
    EveryMatch,
    Latest,
    Average,
    MinMax,
}

/// <summary>One variable-monitor rule: extracts a numeric/text value from matching lines.</summary>
public sealed record VariableMonitorRule(
    Guid Id,
    string Name,
    string? PortName,
    string Pattern,
    bool IsEnabled,
    int Order,
    VariableMonitorValueType ValueType = VariableMonitorValueType.Number,
    string? ValueGroup = null,
    double Scale = 1,
    double Offset = 0,
    string? Unit = null,
    bool PlotEnabled = true,
    string? Color = null,
    string? AxisId = null,
    VariableMonitorSamplingMode SamplingMode = VariableMonitorSamplingMode.EveryMatch,
    int SampleIntervalMs = 20)
{
    public static VariableMonitorRule CreateDefault() => new(
        Guid.NewGuid(),
        string.Empty,
        null,
        string.Empty,
        true,
        0);
}

/// <summary>The latest sample of one rule.</summary>
public sealed record VariableMonitorSample(
    Guid RuleId,
    string Value,
    DateTimeOffset SampledAtUtc,
    long MatchCount);

public enum VariableMonitorRuleState
{
    Waiting,
    Active,
    InvalidRegex,
    RegexTimeout,
    NumericParseError,
    NonFiniteValue,
}

public sealed record VariableMonitorRuleStatus(
    Guid RuleId,
    VariableMonitorRuleState State,
    long MatchCount,
    long NumericSampleCount,
    long NumericParseFailureCount,
    long NonFiniteValueCount,
    long RegexTimeoutCount);

/// <summary>
/// Pure variable-monitor evaluator. Lines arrive incrementally from display snapshots;
/// the first capture group (or the whole match) becomes the variable value. Regex runs use
/// the unified 100 ms timeout and never throw.
/// </summary>
public sealed class VariableMonitorEvaluator
{
    public static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

    private readonly Dictionary<Guid, VariableMonitorSample> _samples = [];
    private readonly Dictionary<Guid, long> _matchCounts = [];
    private readonly Dictionary<Guid, MutableStatus> _statuses = [];
    private readonly Dictionary<Guid, Regex?> _regexes = [];

    public IReadOnlyList<VariableMonitorRule> Rules { get; private set; } = [];

    public bool HasRegexTimedOut { get; private set; }

    public void UpdateRules(IReadOnlyList<VariableMonitorRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        Dictionary<Guid, VariableMonitorRule> previous = Rules.ToDictionary(rule => rule.Id);
        Rules = rules;
        foreach (Guid id in _regexes.Keys.Where(id => rules.All(rule => rule.Id != id)).ToList())
        {
            _samples.Remove(id);
            _matchCounts.Remove(id);
            _statuses.Remove(id);
            _regexes.Remove(id);
        }

        foreach (VariableMonitorRule rule in rules)
        {
            if (previous.TryGetValue(rule.Id, out VariableMonitorRule? oldRule) &&
                HasExtractionSemanticsChanged(oldRule, rule))
            {
                _samples.Remove(rule.Id);
                _matchCounts.Remove(rule.Id);
                _statuses.Remove(rule.Id);
                _regexes.Remove(rule.Id);
            }

            if (_regexes.ContainsKey(rule.Id))
            {
                continue;
            }

            MutableStatus status = new();
            _statuses[rule.Id] = status;
            try
            {
                _regexes[rule.Id] = string.IsNullOrEmpty(rule.Pattern)
                    ? null
                    : new Regex(rule.Pattern, RegexOptions.None, MatchTimeout);
            }
            catch (ArgumentException)
            {
                _regexes[rule.Id] = null;
                status.State = VariableMonitorRuleState.InvalidRegex;
            }
        }
    }

    public IReadOnlyList<VariableNumericSample> AppendLine(string? portName, string text, DateTimeOffset timestampUtc)
    {
        List<VariableNumericSample> numericSamples = [];
        if (string.IsNullOrEmpty(text))
        {
            return numericSamples;
        }

        foreach (VariableMonitorRule rule in Rules)
        {
            if (!rule.IsEnabled || string.IsNullOrEmpty(rule.Pattern))
            {
                continue;
            }

            if (rule.PortName is { Length: > 0 } &&
                !string.Equals(rule.PortName, portName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string? value = MatchValue(rule, text);
            if (value is null)
            {
                continue;
            }

            long count = _matchCounts.TryGetValue(rule.Id, out long existing) ? existing + 1 : 1;
            _matchCounts[rule.Id] = count;
            _samples[rule.Id] = new VariableMonitorSample(rule.Id, value, timestampUtc, count);
            MutableStatus status = _statuses[rule.Id];
            status.MatchCount = count;
            status.State = VariableMonitorRuleState.Active;

            if (rule.ValueType != VariableMonitorValueType.Number)
            {
                continue;
            }

            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
            {
                status.NumericParseFailureCount++;
                status.State = VariableMonitorRuleState.NumericParseError;
                continue;
            }

            double scale = double.IsFinite(rule.Scale) ? rule.Scale : 1;
            double offset = double.IsFinite(rule.Offset) ? rule.Offset : 0;
            double numericValue = parsed * scale + offset;
            if (!double.IsFinite(parsed) || !double.IsFinite(numericValue))
            {
                status.NonFiniteValueCount++;
                status.State = VariableMonitorRuleState.NonFiniteValue;
                continue;
            }

            status.NumericSampleCount++;
            numericSamples.Add(new VariableNumericSample(rule.Id, portName, timestampUtc, numericValue, count));
        }

        return numericSamples;
    }

    public IReadOnlyList<VariableMonitorSample> Samples
    {
        get
        {
            List<VariableMonitorSample> result = [];
            foreach (VariableMonitorRule rule in Rules.Where(rule => rule.IsEnabled).OrderBy(rule => rule.Order))
            {
                if (_samples.TryGetValue(rule.Id, out VariableMonitorSample? sample))
                {
                    result.Add(sample);
                }
            }

            return result;
        }
    }

    public IReadOnlyList<VariableMonitorSample> AllSamples() => [.. _samples.Values];

    public void ClearSamples()
    {
        _samples.Clear();
        _matchCounts.Clear();
        foreach (MutableStatus status in _statuses.Values)
        {
            status.Reset();
        }
    }

    public IReadOnlyList<VariableMonitorRuleStatus> RuleStatuses => [.. Rules.Select(rule =>
    {
        MutableStatus status = _statuses[rule.Id];
        return new VariableMonitorRuleStatus(
            rule.Id,
            status.State,
            status.MatchCount,
            status.NumericSampleCount,
            status.NumericParseFailureCount,
            status.NonFiniteValueCount,
            status.RegexTimeoutCount);
    })];

    private string? MatchValue(VariableMonitorRule rule, string text)
    {
        try
        {
            if (!_regexes.TryGetValue(rule.Id, out Regex? regex) || regex is null)
            {
                return null;
            }

            Match match = regex.Match(text);
            if (!match.Success)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(rule.ValueGroup))
            {
                Group named = match.Groups[rule.ValueGroup];
                if (named.Success)
                {
                    return named.Value;
                }
            }

            return match.Groups.Count > 1 && match.Groups[1].Success ? match.Groups[1].Value : match.Value;
        }
        catch (RegexMatchTimeoutException)
        {
            HasRegexTimedOut = true;
            MutableStatus status = _statuses[rule.Id];
            status.RegexTimeoutCount++;
            status.State = VariableMonitorRuleState.RegexTimeout;
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    internal static bool HasExtractionSemanticsChanged(VariableMonitorRule left, VariableMonitorRule right) =>
        !string.Equals(left.PortName, right.PortName, StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(left.Pattern, right.Pattern, StringComparison.Ordinal) ||
        left.ValueType != right.ValueType ||
        !string.Equals(left.ValueGroup, right.ValueGroup, StringComparison.Ordinal) ||
        left.Scale != right.Scale ||
        left.Offset != right.Offset ||
        left.SamplingMode != right.SamplingMode ||
        left.SampleIntervalMs != right.SampleIntervalMs;

    private sealed class MutableStatus
    {
        public VariableMonitorRuleState State { get; set; }
        public long MatchCount { get; set; }
        public long NumericSampleCount { get; set; }
        public long NumericParseFailureCount { get; set; }
        public long NonFiniteValueCount { get; set; }
        public long RegexTimeoutCount { get; set; }

        public void Reset()
        {
            State = VariableMonitorRuleState.Waiting;
            MatchCount = 0;
            NumericSampleCount = 0;
            NumericParseFailureCount = 0;
            NonFiniteValueCount = 0;
            RegexTimeoutCount = 0;
        }
    }
}
