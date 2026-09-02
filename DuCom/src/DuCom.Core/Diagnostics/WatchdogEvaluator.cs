using System.Text.RegularExpressions;

namespace DuCom.Core.Diagnostics;

/// <summary>Result of one watchdog evaluation pass.</summary>
public sealed record WatchdogFiredRule(
    WatchdogRule Rule,
    DateTimeOffset FiredAtUtc,
    string Reason);

/// <summary>
/// Pure watchdog state machine. Lines are appended incrementally (from display snapshots,
/// never from receive callbacks); <see cref="Check"/> returns the rules whose expected
/// pattern has not been seen within their window. All regex execution is timeout-bounded
/// and exception-safe.
/// </summary>
public sealed class WatchdogEvaluator
{
    public static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

    private readonly Dictionary<Guid, RuleState> _states = [];

    public IReadOnlyList<WatchdogRule> Rules { get; private set; } = [];

    public DateTimeOffset? SessionStartedAtUtc { get; private set; }

    /// <summary>Replaces the rule set. Matching history is preserved for surviving rule ids.</summary>
    public void UpdateRules(IReadOnlyList<WatchdogRule> rules)
    {
        Rules = rules;
        foreach (Guid id in _states.Keys.Where(id => rules.All(rule => rule.Id != id)).ToList())
        {
            _states.Remove(id);
        }
    }

    /// <summary>Records the session start; expectation windows count from here.</summary>
    public void Start(DateTimeOffset startedAtUtc)
    {
        SessionStartedAtUtc = startedAtUtc;
        foreach (WatchdogRule rule in Rules.Where(rule => rule.IsEnabled))
        {
            StateOf(rule).LastMatchUtc ??= startedAtUtc;
        }
    }

    /// <summary>Appends one display line and updates match timestamps. Pure CPU work, no I/O.</summary>
    public void AppendLine(string text, DateTimeOffset timestampUtc)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        foreach (WatchdogRule rule in Rules)
        {
            if (!rule.IsEnabled || string.IsNullOrEmpty(rule.Pattern))
            {
                continue;
            }

            if (IsMatch(rule, text))
            {
                StateOf(rule).LastMatchUtc = timestampUtc;
            }
        }
    }

    /// <summary>Returns the rules that should fire at <paramref name="nowUtc"/>, honoring throttle.</summary>
    public IReadOnlyList<WatchdogFiredRule> Check(DateTimeOffset nowUtc)
    {
        List<WatchdogFiredRule> fired = [];
        foreach (WatchdogRule rule in Rules)
        {
            if (!rule.IsEnabled || rule.ExpectWithinSeconds <= 0)
            {
                continue;
            }

            RuleState state = StateOf(rule);
            DateTimeOffset anchor = state.LastMatchUtc ?? SessionStartedAtUtc ?? nowUtc;
            if (nowUtc - anchor < TimeSpan.FromSeconds(rule.ExpectWithinSeconds))
            {
                continue;
            }

            if (state.LastFiredUtc is { } lastFired &&
                nowUtc - lastFired < TimeSpan.FromSeconds(Math.Max(1, rule.ThrottleSeconds)))
            {
                continue;
            }

            state.LastFiredUtc = nowUtc;
            fired.Add(new WatchdogFiredRule(
                rule,
                nowUtc,
                $"no match for '{rule.Pattern}' within {rule.ExpectWithinSeconds}s"));
        }

        return fired;
    }

    public bool HasRegexTimedOut { get; private set; }

    private RuleState StateOf(WatchdogRule rule)
    {
        if (!_states.TryGetValue(rule.Id, out RuleState? state))
        {
            state = new RuleState();
            _states[rule.Id] = state;
        }

        return state;
    }

    private bool IsMatch(WatchdogRule rule, string text)
    {
        if (rule.Mode == WatchdogMatchMode.Regex)
        {
            try
            {
                RegexOptions options = rule.IsCaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
                return Regex.IsMatch(text, rule.Pattern, options, MatchTimeout);
            }
            catch (RegexMatchTimeoutException)
            {
                HasRegexTimedOut = true;
                return false;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        StringComparison comparison = rule.IsCaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        return text.Contains(rule.Pattern, comparison);
    }

    private sealed class RuleState
    {
        public DateTimeOffset? LastMatchUtc { get; set; }

        public DateTimeOffset? LastFiredUtc { get; set; }
    }
}
