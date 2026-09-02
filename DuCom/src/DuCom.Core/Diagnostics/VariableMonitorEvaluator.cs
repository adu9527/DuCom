namespace DuCom.Core.Diagnostics;

/// <summary>One variable-monitor rule: extracts a numeric/text value from matching lines.</summary>
public sealed record VariableMonitorRule(
    Guid Id,
    string Name,
    string? PortName,
    string Pattern,
    bool IsEnabled,
    int Order)
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

    public IReadOnlyList<VariableMonitorRule> Rules { get; private set; } = [];

    public bool HasRegexTimedOut { get; private set; }

    public void UpdateRules(IReadOnlyList<VariableMonitorRule> rules)
    {
        Rules = rules;
        foreach (Guid id in _samples.Keys.Where(id => rules.All(rule => rule.Id != id)).ToList())
        {
            _samples.Remove(id);
            _matchCounts.Remove(id);
        }
    }

    public void AppendLine(string? portName, string text, DateTimeOffset timestampUtc)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
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
        }
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

    private string? MatchValue(VariableMonitorRule rule, string text)
    {
        try
        {
            System.Text.RegularExpressions.Match match =
                System.Text.RegularExpressions.Regex.Match(text, rule.Pattern, System.Text.RegularExpressions.RegexOptions.None, MatchTimeout);
            if (!match.Success)
            {
                return null;
            }

            return match.Groups.Count > 1 && match.Groups[1].Success ? match.Groups[1].Value : match.Value;
        }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            HasRegexTimedOut = true;
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
