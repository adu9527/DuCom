using DuCom.Core.Parsing;

namespace DuCom.Tests.Parsing;

public sealed class HighlightFilterRuleMatcherTimeoutTests
{
    // Catastrophic-backtracking pattern: matching fails only after exponential work.
    private const string CatastrophicPattern = @"(a+)+b";

    private static string CatastrophicInput(int length) =>
        new string('a', length) + "c";

    [Theory]
    [InlineData(30)]
    [InlineData(50)]
    public void Evaluate_CatastrophicHighlightRegex_ReturnsPlainTextAndFlagsTimeout(int inputLength)
    {
        HighlightFilterRule rule = CreateHighlight("slow", CatastrophicPattern, 0xFF, 0x00, 0x00);

        HighlightFilterEvaluation result = HighlightFilterRuleMatcher.Evaluate([rule], CatastrophicInput(inputLength));

        Assert.True(result.IsVisible);
        Assert.True(result.HasRegexTimeout);
        HighlightRun run = Assert.Single(result.HighlightRuns);
        Assert.Equal(CatastrophicInput(inputLength), run.Text);
        Assert.False(run.HasForeground);
    }

    [Fact]
    public void Evaluate_CatastrophicFilterRegexAlone_DoesNotHideContent()
    {
        HighlightFilterRule filter = CreateFilter("slow", CatastrophicPattern);

        HighlightFilterEvaluation result = HighlightFilterRuleMatcher.Evaluate([filter], CatastrophicInput(40));

        Assert.True(result.IsVisible);
        Assert.True(result.HasRegexTimeout);
    }

    [Fact]
    public void Evaluate_CatastrophicFilterPlusHealthyNonMatchingFilter_StillHidesLine()
    {
        HighlightFilterRule slowFilter = CreateFilter("slow", CatastrophicPattern);
        HighlightFilterRule healthyFilter = CreateFilter("errors", "error", RuleMatchMode.Contains);

        HighlightFilterEvaluation result = HighlightFilterRuleMatcher.Evaluate([slowFilter, healthyFilter], CatastrophicInput(40));

        Assert.False(result.IsVisible);
    }

    [Fact]
    public void Evaluate_HealthyFilterMatchWithCatastrophicFilter_ShowsLineAndFlagsTimeout()
    {
        HighlightFilterRule slowFilter = CreateFilter("slow", CatastrophicPattern);
        HighlightFilterRule healthyFilter = CreateFilter("errors", "error", RuleMatchMode.Contains);

        HighlightFilterEvaluation result = HighlightFilterRuleMatcher.Evaluate([slowFilter, healthyFilter], "error: " + CatastrophicInput(40));

        Assert.True(result.IsVisible);
        Assert.True(result.HasRegexTimeout);
    }

    [Fact]
    public void Evaluate_LongInputWithNormalRegex_CompletesWithoutTimeout()
    {
        string text = string.Join(" ", Enumerable.Repeat("value=123", 2_000));
        HighlightFilterRule rule = CreateHighlight("numbers", @"\d+", 0xFF, 0x00, 0x00);

        HighlightFilterEvaluation result = HighlightFilterRuleMatcher.Evaluate([rule], text);

        Assert.True(result.IsVisible);
        Assert.False(result.HasRegexTimeout);
        Assert.Contains(result.HighlightRuns, run => run.Text == "123" && run.HasForeground);
    }

    [Fact]
    public void Evaluate_LongContainsPattern_CompletesWithoutTimeout()
    {
        string text = new string('x', 20_000) + "needle" + new string('y', 20_000);
        HighlightFilterRule filter = CreateFilter("needle", "needle", RuleMatchMode.Contains);

        HighlightFilterEvaluation result = HighlightFilterRuleMatcher.Evaluate([filter], text);

        Assert.True(result.IsVisible);
        Assert.False(result.HasRegexTimeout);
    }

    [Fact]
    public void Evaluate_CatastrophicHighlightAndFilterTogether_DoesNotThrowAndStaysVisible()
    {
        HighlightFilterRule slowHighlight = CreateHighlight("slowH", CatastrophicPattern, 0xFF, 0x00, 0x00);
        HighlightFilterRule slowFilter = CreateFilter("slowF", CatastrophicPattern);

        HighlightFilterEvaluation result = HighlightFilterRuleMatcher.Evaluate([slowHighlight, slowFilter], CatastrophicInput(35));

        Assert.True(result.IsVisible);
        Assert.True(result.HasRegexTimeout);
    }

    [Fact]
    public void IsMatch_CatastrophicRegex_ReturnsFalseWithoutThrowing()
    {
        HighlightFilterRule rule = CreateFilter("slow", CatastrophicPattern);

        bool matched = HighlightFilterRuleMatcher.IsMatch(rule, CatastrophicInput(40));

        Assert.False(matched);
    }

    [Fact]
    public void MatchTimeout_IsHundredMilliseconds()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(100), HighlightFilterRuleMatcher.MatchTimeout);
    }

    [Fact]
    public void Evaluate_NormalRegexStillMatches_NoRegression()
    {
        HighlightFilterRule rule = CreateHighlight("error", @"error\d+", 0xFF, 0x00, 0x00);

        HighlightFilterEvaluation result = HighlightFilterRuleMatcher.Evaluate([rule], "error42 happened");

        Assert.True(result.IsVisible);
        Assert.False(result.HasRegexTimeout);
        Assert.Equal("error42", result.HighlightRuns[0].Text);
        Assert.True(result.HighlightRuns[0].HasForeground);
    }

    private static HighlightFilterRule CreateHighlight(
        string name,
        string pattern,
        byte r,
        byte g,
        byte b,
        bool isCaseSensitive = false)
    {
        return new HighlightFilterRule(
            Guid.NewGuid(),
            name,
            HighlightFilterRuleKind.Highlight,
            RuleMatchMode.Regex,
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

    private static HighlightFilterRule CreateFilter(string name, string pattern, RuleMatchMode mode = RuleMatchMode.Regex, bool isCaseSensitive = false)
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
