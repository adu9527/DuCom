using DuCom.Core.Parsing;
using Xunit;

namespace DuCom.Core.Tests.Parsing;

public class HighlightBackgroundTests
{
    private static HighlightFilterRule Rule(
        string pattern,
        (byte, byte, byte)? fg = null,
        (byte, byte, byte)? bg = null) => new(
            Guid.NewGuid(),
            pattern,
            HighlightFilterRuleKind.Highlight,
            RuleMatchMode.Contains,
            pattern,
            IsCaseSensitive: false,
            IsEnabled: true,
            fg?.Item1, fg?.Item2, fg?.Item3,
            bg?.Item1, bg?.Item2, bg?.Item3);

    private static IReadOnlyList<StyleRun> Compose(string text, params HighlightFilterRule[] rules) =>
        StyledTextComposer.Compose(
            [new AnsiRun(text, AnsiStyle.Default)],
            HighlightFilterRuleMatcher.Evaluate(rules, text).HighlightRuns);

    [Fact]
    public void RuleBackgroundOnly_IsCarriedIntoRuns()
    {
        IReadOnlyList<StyleRun> runs = Compose("warn here", Rule("warn", bg: (10, 20, 30)));

        StyleRun run = Assert.Single(runs, item => item.Text == "warn");
        Assert.True(run.HasBackground);
        Assert.Equal((byte)20, run.BackgroundG);
        Assert.False(run.HasForeground);
    }

    [Fact]
    public void RuleForegroundAndBackground_Combine()
    {
        IReadOnlyList<StyleRun> runs = Compose("error x", Rule("error", fg: (255, 0, 0), bg: (1, 2, 3)));

        StyleRun run = Assert.Single(runs, item => item.Text == "error");
        Assert.True(run.HasForeground);
        Assert.True(run.HasBackground);
        Assert.Equal((byte)3, run.BackgroundB);
    }

    [Fact]
    public void NoRuleBackground_StaysNull()
    {
        IReadOnlyList<StyleRun> runs = Compose("plain");

        StyleRun run = Assert.Single(runs);
        Assert.False(run.HasBackground);
        Assert.Null(run.BackgroundR);
    }

    [Fact]
    public void OverlappingRules_FirstDeclaredWinsForBothChannels()
    {
        List<HighlightFilterRule> rules =
        [
            Rule("abcdef", fg: (100, 100, 100), bg: (9, 9, 9)),
            Rule("cde", fg: (200, 200, 200), bg: (8, 8, 8)),
        ];
        var evaluation = HighlightFilterRuleMatcher.Evaluate(rules, "abcdef");

        Assert.All(evaluation.HighlightRuns.Where(run => run.Text == "abcdef"), run =>
        {
            Assert.Equal((byte)100, run.ForegroundR);
            Assert.Equal((byte)9, run.BackgroundR);
        });
        Assert.DoesNotContain(evaluation.HighlightRuns, run => run.Text == "cde");
    }

    [Fact]
    public void AnsiExplicitBackgroundWinsOverRuleBackground()
    {
        var ansiStyle = new AnsiStyle(null, null, null, 50, 60, 70, false, false, false);
        List<HighlightFilterRule> rules = [Rule("body", bg: (11, 12, 13))];

        IReadOnlyList<StyleRun> runs = StyledTextComposer.Compose(
            [new AnsiRun("body", ansiStyle)],
            HighlightFilterRuleMatcher.Evaluate(rules, "body").HighlightRuns);

        StyleRun run = Assert.Single(runs);
        Assert.True(run.HasBackground);
        Assert.Equal((byte)50, run.BackgroundR);
        Assert.Equal((byte)70, run.BackgroundB);
    }

    [Fact]
    public void AnsiWithoutBackground_UsesRuleBackground()
    {
        var ansiStyle = new AnsiStyle(250, 251, 252, null, null, null, false, false, false);
        List<HighlightFilterRule> rules = [Rule("body", bg: (11, 12, 13))];

        IReadOnlyList<StyleRun> runs = StyledTextComposer.Compose(
            [new AnsiRun("body", ansiStyle)],
            HighlightFilterRuleMatcher.Evaluate(rules, "body").HighlightRuns);

        StyleRun run = Assert.Single(runs);
        Assert.Equal((byte)250, run.ForegroundR); // ANSI foreground precedence retained
        Assert.True(run.HasBackground);
        Assert.Equal((byte)11, run.BackgroundR);
    }
}
