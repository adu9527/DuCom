using DuCom.Core.Parsing;

namespace DuCom.Core.Tests.Parsing;

public sealed class HighlightFilterRuleMatcherTests
{
    [Fact]
    public void Evaluate_NoRules_ReturnsVisiblePlainRun()
    {
        HighlightFilterEvaluation result = HighlightFilterRuleMatcher.Evaluate([], "hello world");

        Assert.True(result.IsVisible);
        HighlightRun run = Assert.Single(result.HighlightRuns);
        Assert.Equal("hello world", run.Text);
        Assert.False(run.HasForeground);
    }

    [Fact]
    public void Evaluate_NullRules_ReturnsVisiblePlainRun()
    {
        HighlightFilterEvaluation result = HighlightFilterRuleMatcher.Evaluate(null, "hello world");

        Assert.True(result.IsVisible);
        HighlightRun run = Assert.Single(result.HighlightRuns);
        Assert.Equal("hello world", run.Text);
    }

    [Fact]
    public void Evaluate_RegexHighlight_SplitsRunWithColor()
    {
        HighlightFilterRule rule = CreateHighlight("error", "error", RuleMatchMode.Regex, 0xFF, 0x00, 0x00);

        HighlightFilterEvaluation result = HighlightFilterRuleMatcher.Evaluate([rule], "error: module failed");

        Assert.True(result.IsVisible);
        Assert.Equal(2, result.HighlightRuns.Count);
        Assert.Equal("error", result.HighlightRuns[0].Text);
        Assert.True(result.HighlightRuns[0].HasForeground);
        Assert.Equal((byte?)0xFF, result.HighlightRuns[0].ForegroundR);
        Assert.Equal((byte?)0x00, result.HighlightRuns[0].ForegroundG);
        Assert.Equal((byte?)0x00, result.HighlightRuns[0].ForegroundB);
        Assert.Equal(": module failed", result.HighlightRuns[1].Text);
        Assert.False(result.HighlightRuns[1].HasForeground);
    }

    [Fact]
    public void Evaluate_ContainsHighlight_IsCaseInsensitiveByDefault()
    {
        HighlightFilterRule rule = CreateHighlight("warn", "WARN", RuleMatchMode.Contains, 0xFF, 0xA5, 0x00);

        HighlightFilterEvaluation result = HighlightFilterRuleMatcher.Evaluate([rule], "Warning: low memory");

        Assert.True(result.IsVisible);
        Assert.Contains(result.HighlightRuns, run =>
            run.Text.Equals("Warn", StringComparison.Ordinal) &&
            run.ForegroundR == (byte?)0xFF && run.ForegroundG == (byte?)0xA5 && run.ForegroundB == (byte?)0x00);
    }

    [Fact]
    public void Evaluate_ContainsCaseSensitive_NoMatchWhenCaseDiffers()
    {
        HighlightFilterRule rule = CreateHighlight("info", "INFO", RuleMatchMode.Contains, 0x00, 0x00, 0xFF, isCaseSensitive: true);

        HighlightFilterEvaluation result = HighlightFilterRuleMatcher.Evaluate([rule], "Information only");

        Assert.True(result.IsVisible);
        Assert.DoesNotContain(result.HighlightRuns, run => run.Text.Equals("Info", StringComparison.Ordinal));
    }

    [Fact]
    public void Evaluate_MultipleMatches_AppliesColorToEach()
    {
        HighlightFilterRule rule = CreateHighlight("word", "\\w+", RuleMatchMode.Regex, 0x00, 0xFF, 0x00);

        HighlightFilterEvaluation result = HighlightFilterRuleMatcher.Evaluate([rule], "a b");

        Assert.True(result.IsVisible);
        Assert.Equal(3, result.HighlightRuns.Count);
        Assert.Equal("a", result.HighlightRuns[0].Text);
        Assert.True(result.HighlightRuns[0].HasForeground);
        Assert.Equal(" ", result.HighlightRuns[1].Text);
        Assert.False(result.HighlightRuns[1].HasForeground);
        Assert.Equal("b", result.HighlightRuns[2].Text);
        Assert.True(result.HighlightRuns[2].HasForeground);
    }

    [Fact]
    public void Evaluate_NonOverlappingRules_AppliesBoth()
    {
        HighlightFilterRule first = CreateHighlight("letters", "[a-zA-Z]+", RuleMatchMode.Regex, 0x00, 0x00, 0xFF);
        HighlightFilterRule second = CreateHighlight("digits", "[0-9]+", RuleMatchMode.Regex, 0xFF, 0x00, 0x00);

        HighlightFilterEvaluation result = HighlightFilterRuleMatcher.Evaluate([first, second], "abc123");

        Assert.True(result.IsVisible);
        Assert.Equal(2, result.HighlightRuns.Count);
        Assert.Equal("abc", result.HighlightRuns[0].Text);
        Assert.Equal((byte?)0xFF, result.HighlightRuns[0].ForegroundB);
        Assert.Equal("123", result.HighlightRuns[1].Text);
        Assert.Equal((byte?)0xFF, result.HighlightRuns[1].ForegroundR);
    }

    [Fact]
    public void Evaluate_OverlappingRules_FirstRulePreventsSecondOverlap()
    {
        HighlightFilterRule first = CreateHighlight("abc", "abc", RuleMatchMode.Regex, 0x00, 0x00, 0xFF);
        HighlightFilterRule second = CreateHighlight("alphanumeric", "[a-zA-Z0-9]+", RuleMatchMode.Regex, 0xFF, 0x00, 0x00);

        HighlightFilterEvaluation result = HighlightFilterRuleMatcher.Evaluate([first, second], "abc123");

        Assert.True(result.IsVisible);
        Assert.Equal(2, result.HighlightRuns.Count);
        Assert.Equal("abc", result.HighlightRuns[0].Text);
        Assert.Equal((byte?)0xFF, result.HighlightRuns[0].ForegroundB);
        Assert.Equal("123", result.HighlightRuns[1].Text);
        Assert.False(result.HighlightRuns[1].HasForeground);
    }

    [Fact]
    public void Evaluate_DisabledHighlight_IsIgnored()
    {
        HighlightFilterRule rule = CreateHighlight("match", "match", RuleMatchMode.Contains, 0xFF, 0x00, 0x00);
        rule = rule with { IsEnabled = false };

        HighlightFilterEvaluation result = HighlightFilterRuleMatcher.Evaluate([rule], "match");

        Assert.True(result.IsVisible);
        HighlightRun run = Assert.Single(result.HighlightRuns);
        Assert.False(run.HasForeground);
    }

    [Fact]
    public void Evaluate_FilterRule_HidesNonMatchingLine()
    {
        HighlightFilterRule filter = CreateFilter("errors only", "error", RuleMatchMode.Contains);

        HighlightFilterEvaluation result = HighlightFilterRuleMatcher.Evaluate([filter], "this is fine");

        Assert.False(result.IsVisible);
        Assert.Empty(result.HighlightRuns);
    }

    [Fact]
    public void Evaluate_FilterRule_ShowsMatchingLine()
    {
        HighlightFilterRule filter = CreateFilter("errors only", "error", RuleMatchMode.Contains);

        HighlightFilterEvaluation result = HighlightFilterRuleMatcher.Evaluate([filter], "error: failure");

        Assert.True(result.IsVisible);
        Assert.Single(result.HighlightRuns);
    }

    [Fact]
    public void Evaluate_DisabledFilter_DoesNotHide()
    {
        HighlightFilterRule filter = CreateFilter("errors only", "error", RuleMatchMode.Contains);
        filter = filter with { IsEnabled = false };

        HighlightFilterEvaluation result = HighlightFilterRuleMatcher.Evaluate([filter], "other text");

        Assert.True(result.IsVisible);
    }

    [Fact]
    public void Evaluate_MultipleFilters_AnyMatchShowsLine()
    {
        HighlightFilterRule filter1 = CreateFilter("errors", "error", RuleMatchMode.Contains);
        HighlightFilterRule filter2 = CreateFilter("warnings", "warn", RuleMatchMode.Contains);

        HighlightFilterEvaluation result = HighlightFilterRuleMatcher.Evaluate([filter1, filter2], "warning: heat");

        Assert.True(result.IsVisible);
    }

    [Fact]
    public void Evaluate_FilterAndHighlightTogether_AppliesBoth()
    {
        HighlightFilterRule filter = CreateFilter("errors", "error", RuleMatchMode.Contains);
        HighlightFilterRule highlight = CreateHighlight("number", "[0-9]+", RuleMatchMode.Regex, 0xFF, 0x00, 0x00);

        HighlightFilterEvaluation result = HighlightFilterRuleMatcher.Evaluate([filter, highlight], "error: code 42");

        Assert.True(result.IsVisible);
        Assert.Contains(result.HighlightRuns, run => run.Text == "42" && run.HasForeground);
    }

    [Fact]
    public void Evaluate_MalformedRegexAtRuntime_DoesNotThrow()
    {
        HighlightFilterRule rule = CreateHighlight("bad", "[invalid", RuleMatchMode.Regex, 0xFF, 0x00, 0x00);

        HighlightFilterEvaluation result = HighlightFilterRuleMatcher.Evaluate([rule], "[invalid text");

        Assert.True(result.IsVisible);
        HighlightRun run = Assert.Single(result.HighlightRuns);
        Assert.Equal("[invalid text", run.Text);
    }

    [Fact]
    public void Evaluate_EmptyText_ReturnsEmptyVisibleRun()
    {
        HighlightFilterRule rule = CreateHighlight("x", "x", RuleMatchMode.Contains, 0xFF, 0x00, 0x00);

        HighlightFilterEvaluation result = HighlightFilterRuleMatcher.Evaluate([rule], string.Empty);

        Assert.True(result.IsVisible);
        HighlightRun run = Assert.Single(result.HighlightRuns);
        Assert.Equal(string.Empty, run.Text);
    }

    private static HighlightFilterRule CreateHighlight(
        string name,
        string pattern,
        RuleMatchMode mode,
        byte r,
        byte g,
        byte b,
        bool isCaseSensitive = false)
    {
        return new HighlightFilterRule(
            Guid.NewGuid(),
            name,
            HighlightFilterRuleKind.Highlight,
            mode,
            pattern,
            isCaseSensitive,
            true,
            r,
            g,
            b,
            null,
            null,
            null);
    }

    private static HighlightFilterRule CreateFilter(string name, string pattern, RuleMatchMode mode, bool isCaseSensitive = false)
    {
        return new HighlightFilterRule(
            Guid.NewGuid(),
            name,
            HighlightFilterRuleKind.Filter,
            mode,
            pattern,
            isCaseSensitive,
            true,
            null,
            null,
            null,
            null,
            null,
            null);
    }
}
