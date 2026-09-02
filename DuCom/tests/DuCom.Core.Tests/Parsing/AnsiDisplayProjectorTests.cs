using DuCom.Core.Parsing;
using Xunit;

namespace DuCom.Core.Tests.Parsing;

public class AnsiDisplayProjectorTests
{
    [Fact]
    public void StyleCarriedAcrossSegmentsWithoutEscape()
    {
        AnsiDisplayProjector projector = new();

        AnsiProjection first = projector.Project("\u001B[31mred", null);
        AnsiProjection second = projector.Project(" still red", null);

        Assert.Equal("red", first.DisplayText);
        Assert.Equal(" still red", second.DisplayText);

        StyleRun sourceStyle = first.Runs.Single(run => run.Text == "red");
        StyleRun carriedStyle = second.Runs[^1];
        Assert.True(carriedStyle.HasForeground);
        Assert.Equal(sourceStyle.ForegroundR, carriedStyle.ForegroundR);
        Assert.Equal(sourceStyle.ForegroundG, carriedStyle.ForegroundG);
        Assert.Equal(sourceStyle.ForegroundB, carriedStyle.ForegroundB);
        Assert.Equal(sourceStyle.BackgroundR, carriedStyle.BackgroundR);
    }

    [Fact]
    public void ResetSequenceRestoresDefaultForLaterSegments()
    {
        AnsiDisplayProjector projector = new();
        projector.Project("\u001B[31mred", null);

        AnsiProjection afterReset = projector.Project("plain\u001B[0m|", null);

        Assert.Equal("plain|", afterReset.DisplayText);
        Assert.False(afterReset.Runs[^1].HasForeground);

        AnsiProjection nextSegment = projector.Project("after", null);
        Assert.All(nextSegment.Runs, run => Assert.False(run.HasForeground));
    }

    [Fact]
    public void TruncatedCsiSplitAcrossSegmentsMatchesDirectParse()
    {
        AnsiDisplayProjector splitProjector = new();
        AnsiProjection head = splitProjector.Project("a\u001B[3", null);
        AnsiProjection tail = splitProjector.Project("1mbright", null);

        AnsiDisplayProjector directProjector = new();
        AnsiProjection direct = directProjector.Project("\u001B[31mbright", null);

        Assert.Equal("a", head.DisplayText);
        Assert.Equal("bright", tail.DisplayText);

        StyleRun splitRun = tail.Runs.Single(run => run.Text == "bright");
        StyleRun directRun = direct.Runs.Single(run => run.Text == "bright");
        Assert.Equal(directRun.HasForeground, splitRun.HasForeground);
        Assert.Equal(directRun.ForegroundR, splitRun.ForegroundR);
        Assert.Equal(directRun.ForegroundG, splitRun.ForegroundG);
        Assert.Equal(directRun.ForegroundB, splitRun.ForegroundB);
    }

    [Fact]
    public void ResetClearsPendingState()
    {
        AnsiDisplayProjector projector = new();
        projector.Project("x\u001B[3", []); // truncated sequence left pending

        projector.Reset();
        AnsiProjection projection = projector.Project("[31mnot a color code", []);

        Assert.Equal("[31mnot a color code", projection.DisplayText);
        Assert.All(projection.Runs, run =>
        {
            Assert.False(run.HasForeground);
            Assert.False(run.Bold);
        });
    }

    [Fact]
    public void HexLikeTextIsNeverInterpretedAsAnsi()
    {
        AnsiDisplayProjector projector = new();
        string hexText = "1B 5B 33 31 6D";

        AnsiProjection projection = projector.Project(hexText, []);

        Assert.Equal(hexText, projection.DisplayText);
        StyleRun run = Assert.Single(projection.Runs);
        Assert.False(run.HasForeground);
        Assert.False(run.Bold);
        Assert.False(run.Inverse);
    }

    [Fact]
    public void HighlightRulesColorNeutralPlainSegmentsThroughFastPath()
    {
        AnsiDisplayProjector projector = new();
        List<HighlightFilterRule> rules =
        [
            CreateRule(pattern: "warn", fg: (255, 200, 0), bg: (40, 40, 40)),
        ];

        AnsiProjection projection = projector.Project("a warn b", rules);

        StyleRun highlighted = projection.Runs.Single(run => run.Text == "warn");
        Assert.Equal((byte)255, highlighted.ForegroundR);
        Assert.True(highlighted.HasBackground);
        Assert.Equal((byte)40, highlighted.BackgroundB);
    }

    [Fact]
    public void ProjectionReturnsFilterVisibilityFromTheSameRuleEvaluation()
    {
        AnsiDisplayProjector projector = new();
        List<HighlightFilterRule> rules =
        [
            CreateRule("keep", null, null, HighlightFilterRuleKind.Filter),
        ];

        AnsiProjection hidden = projector.Project("drop", rules);
        AnsiProjection visible = projector.Project("keep this", rules);

        Assert.False(hidden.IsVisible);
        Assert.Empty(hidden.Runs);
        Assert.True(visible.IsVisible);
        Assert.NotEmpty(visible.Runs);
    }

    [Fact]
    public void ActiveStyleContinuesThroughNeutralFastPathCandidateSegments()
    {
        // The fast path must not trigger while a style is active; the second segment has no
        // ESC, yet must stay styled because persistent parser state carries over.
        AnsiDisplayProjector projector = new();
        List<HighlightFilterRule> rules = [];
        _ = projector.Project("\u001B[32mgreen", rules);

        AnsiProjection continuation = projector.Project("tail", rules);

        Assert.Equal("tail", continuation.DisplayText);
        StyleRun run = Assert.Single(continuation.Runs);
        StyleRun reference = ProjectSingleSegmentForComparison("\u001B[32mtail");
        Assert.Equal(reference.HasForeground, run.HasForeground);
        Assert.Equal(reference.ForegroundR, run.ForegroundR);
        Assert.Equal(reference.ForegroundG, run.ForegroundG);
        Assert.Equal(reference.ForegroundB, run.ForegroundB);
    }

    private static StyleRun ProjectSingleSegmentForComparison(string segment)
    {
        AnsiDisplayProjector fresh = new();
        AnsiProjection projection = fresh.Project(segment, []);
        return projection.Runs.Single(run => run.Text == "tail");
    }

    private static HighlightFilterRule CreateRule(
        string pattern,
        (byte, byte, byte)? fg,
        (byte, byte, byte)? bg,
        HighlightFilterRuleKind kind = HighlightFilterRuleKind.Highlight) => new(
            Guid.NewGuid(),
            pattern,
            kind,
            RuleMatchMode.Contains,
            pattern,
            IsCaseSensitive: false,
            IsEnabled: true,
            fg?.Item1, fg?.Item2, fg?.Item3,
            bg?.Item1, bg?.Item2, bg?.Item3);
}
