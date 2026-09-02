using System.Text.RegularExpressions;

namespace DuCom.Core.Parsing;

public static class HighlightFilterRuleMatcher
{
    /// <summary>
    /// Unified regex execution timeout for highlight and filter rules. Catastrophic
    /// patterns are skipped after this budget instead of blocking the frame-pull path.
    /// </summary>
    public static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

    public static HighlightFilterEvaluation Evaluate(IReadOnlyList<HighlightFilterRule>? rules, string text)
    {
        bool isVisible = IsVisible(rules, text, out bool hasRegexTimeout);
        if (!isVisible)
        {
            return new HighlightFilterEvaluation(false, Array.Empty<HighlightRun>(), hasRegexTimeout);
        }

        IReadOnlyList<HighlightRun> runs = BuildHighlightRuns(rules, text, ref hasRegexTimeout);
        return new HighlightFilterEvaluation(true, runs, hasRegexTimeout);
    }

    public static bool IsMatch(HighlightFilterRule rule, string text)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(rule.Pattern))
        {
            return false;
        }

        try
        {
            if (rule.Mode == RuleMatchMode.Regex)
            {
                RegexOptions options = rule.IsCaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
                return Regex.IsMatch(text, rule.Pattern, options, MatchTimeout);
            }
            else
            {
                StringComparison comparison = rule.IsCaseSensitive
                    ? StringComparison.Ordinal
                    : StringComparison.OrdinalIgnoreCase;
                return text.Contains(rule.Pattern, comparison);
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsVisible(IReadOnlyList<HighlightFilterRule>? rules, string text, out bool hasRegexTimeout)
    {
        hasRegexTimeout = false;

        if (rules is null || rules.Count == 0)
        {
            return true;
        }

        List<HighlightFilterRule> enabledFilterRules = rules
            .Where(rule => rule.IsEnabled && rule.Kind == HighlightFilterRuleKind.Filter)
            .ToList();

        if (enabledFilterRules.Count == 0)
        {
            return true;
        }

        bool anyConclusiveNonMatch = false;
        foreach (HighlightFilterRule rule in enabledFilterRules)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(rule.Pattern))
            {
                anyConclusiveNonMatch = true;
                continue;
            }

            if (TryRegexMatch(text, rule, out bool matched, out bool timedOut))
            {
                if (matched)
                {
                    return true;
                }

                anyConclusiveNonMatch = true;
            }
            else if (timedOut)
            {
                hasRegexTimeout = true;
            }
            else
            {
                // Invalid pattern or other evaluated failure is conclusively not a match.
                anyConclusiveNonMatch = true;
            }
        }

        // Fail open only when every enabled filter rule timed out and none produced a
        // conclusive decision: a catastrophic regex must not permanently hide all content.
        return hasRegexTimeout && !anyConclusiveNonMatch;
    }

    private static bool TryRegexMatch(string text, HighlightFilterRule rule, out bool matched, out bool timedOut)
    {
        matched = false;
        timedOut = false;
        RegexOptions options = rule.IsCaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
        try
        {
            matched = Regex.IsMatch(text, rule.Pattern, options, MatchTimeout);
            return true;
        }
        catch (RegexMatchTimeoutException)
        {
            timedOut = true;
            return false;
        }
        catch (ArgumentException)
        {
            matched = false;
            return true;
        }
    }

    private static List<HighlightRun> BuildHighlightRuns(IReadOnlyList<HighlightFilterRule>? rules, string text, ref bool hasRegexTimeout)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [HighlightRun.Plain(string.Empty)];
        }

        List<HighlightFilterRule> highlightRules = rules?
            .Where(rule => rule.IsEnabled && rule.Kind == HighlightFilterRuleKind.Highlight)
            .ToList() ?? [];

        if (highlightRules.Count == 0)
        {
            return [HighlightRun.Plain(text)];
        }

        List<HighlightInterval> intervals = [];
        foreach (HighlightFilterRule rule in highlightRules)
        {
            FindAndApplyMatches(rule, text, intervals, ref hasRegexTimeout);
        }

        return BuildRunsFromIntervals(text, intervals);
    }

    private static void FindAndApplyMatches(
        HighlightFilterRule rule,
        string text,
        List<HighlightInterval> intervals,
        ref bool hasRegexTimeout)
    {
        if (string.IsNullOrEmpty(rule.Pattern))
        {
            return;
        }

        List<HighlightInterval> matches = [];
        try
        {
            if (rule.Mode == RuleMatchMode.Regex)
            {
                RegexOptions options = rule.IsCaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
                foreach (Match match in Regex.Matches(text, rule.Pattern, options, MatchTimeout))
                {
                    if (match.Success && match.Length > 0)
                    {
                        matches.Add(new HighlightInterval(
                            match.Index,
                            match.Length,
                            rule.ForegroundR,
                            rule.ForegroundG,
                            rule.ForegroundB,
                            rule.BackgroundR,
                            rule.BackgroundG,
                            rule.BackgroundB,
                            rule.Bold,
                            rule.Italic));
                    }
                }
            }
            else
            {
                StringComparison comparison = rule.IsCaseSensitive
                    ? StringComparison.Ordinal
                    : StringComparison.OrdinalIgnoreCase;
                int index = 0;
                while (index < text.Length)
                {
                    int found = text.IndexOf(rule.Pattern, index, comparison);
                    if (found < 0)
                    {
                        break;
                    }

                    matches.Add(new HighlightInterval(
                        found,
                        rule.Pattern.Length,
                        rule.ForegroundR,
                        rule.ForegroundG,
                        rule.ForegroundB,
                        rule.BackgroundR,
                        rule.BackgroundG,
                        rule.BackgroundB,
                        rule.Bold,
                        rule.Italic));
                    index = found + 1;
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // A timed-out highlight rule contributes no runs; the text stays visible unhighlighted.
            hasRegexTimeout = true;
            return;
        }
        catch
        {
            return;
        }

        foreach (HighlightInterval match in matches)
        {
            bool overlaps = intervals.Any(existing =>
                match.Start < existing.Start + existing.Length &&
                match.Start + match.Length > existing.Start);
            if (!overlaps)
            {
                intervals.Add(match);
            }
        }
    }

    private static List<HighlightRun> BuildRunsFromIntervals(string text, List<HighlightInterval> intervals)
    {
        intervals.Sort((a, b) => a.Start.CompareTo(b.Start));
        List<HighlightRun> runs = [];
        int position = 0;

        foreach (HighlightInterval interval in intervals)
        {
            if (interval.Start < position)
            {
                continue;
            }

            if (interval.Start > position)
            {
                runs.Add(HighlightRun.Plain(text.Substring(position, interval.Start - position)));
            }

            runs.Add(new HighlightRun(
                text.Substring(interval.Start, interval.Length),
                interval.ForegroundR,
                interval.ForegroundG,
                interval.ForegroundB,
                interval.BackgroundR,
                interval.BackgroundG,
                interval.BackgroundB,
                interval.Bold,
                interval.Italic));
            position = interval.Start + interval.Length;

            if (position >= text.Length)
            {
                break;
            }
        }

        if (position < text.Length)
        {
            runs.Add(HighlightRun.Plain(text[position..]));
        }

        return runs;
    }

    private readonly record struct HighlightInterval(
        int Start,
        int Length,
        byte? ForegroundR,
        byte? ForegroundG,
        byte? ForegroundB,
        byte? BackgroundR,
        byte? BackgroundG,
        byte? BackgroundB,
        bool Bold,
        bool Italic);
}
