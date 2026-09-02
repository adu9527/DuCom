using DuCom.Core.Parsing;
using Xunit;

namespace DuCom.Core.Tests.Parsing;

public class StyledTextComposerTests
{
    private static HighlightRun PlainHighlightRun(string text) => new(text, null, null, null);

    [Fact]
    public void Compose_WithoutHighlight_ReturnsAnsiStyledRuns()
    {
        List<AnsiRun> ansiRuns =
        [
            new("INFO ", AnsiStyle.Default),
            new("ERROR", new AnsiStyle(255, 0, 0, null, null, null, true, false, false)),
        ];

        IReadOnlyList<StyleRun> result = StyledTextComposer.Compose(ansiRuns, [PlainHighlightRun("INFO ERROR")]);

        Assert.Equal(2, result.Count);
        Assert.Equal("INFO ", result[0].Text);
        Assert.False(result[0].HasForeground);
        Assert.Equal("ERROR", result[1].Text);
        Assert.True(result[1].HasForeground);
        Assert.Equal((byte)255, result[1].ForegroundR);
        Assert.True(result[1].Bold);
    }

    [Fact]
    public void Compose_EmptyInputs_ReturnsSingleEmptyRun()
    {
        IReadOnlyList<StyleRun> result = StyledTextComposer.Compose([], []);

        Assert.Single(result);
        Assert.Equal(string.Empty, result[0].Text);
    }

    [Fact]
    public void Compose_HighlightInsidePlainRun_SplitsAtBoundaries()
    {
        List<AnsiRun> ansiRuns = [new("value=123 end", AnsiStyle.Default)];
        List<HighlightRun> highlightRuns =
        [
            PlainHighlightRun("value="),
            new("123", 0, 200, 0),
            PlainHighlightRun(" end"),
        ];

        IReadOnlyList<StyleRun> result = StyledTextComposer.Compose(ansiRuns, highlightRuns);

        Assert.Equal(3, result.Count);
        Assert.Equal("value=", result[0].Text);
        Assert.False(result[0].HasForeground);
        Assert.Equal("123", result[1].Text);
        Assert.Equal((byte)0, result[1].ForegroundR);
        Assert.Equal((byte)200, result[1].ForegroundG);
        Assert.Equal((byte)0, result[1].ForegroundB);
        Assert.Equal(" end", result[2].Text);
        Assert.False(result[2].HasForeground);
    }

    [Fact]
    public void Compose_AnsiForegroundWinsOverHighlight()
    {
        const string text = "ab";
        List<AnsiRun> ansiRuns = [new(text, new AnsiStyle(10, 10, 10, null, null, null, false, false, false))];
        List<HighlightRun> highlightRuns = [new(text, 250, 250, 250)];

        IReadOnlyList<StyleRun> result = StyledTextComposer.Compose(ansiRuns, highlightRuns);

        StyleRun run = Assert.Single(result);
        Assert.Equal((byte)10, run.ForegroundR);
    }

    [Fact]
    public void Compose_BackgroundFromAnsi_IsKept()
    {
        var styled = new AnsiStyle(null, null, null, 20, 40, 60, false, false, false);

        IReadOnlyList<StyleRun> result = StyledTextComposer.Compose([new("bg", styled)], [PlainHighlightRun("bg")]);

        StyleRun run = Assert.Single(result);
        Assert.True(run.HasBackground);
        Assert.Equal((byte)20, run.BackgroundR);
        Assert.Equal((byte)40, run.BackgroundG);
        Assert.Equal((byte)60, run.BackgroundB);
    }

    [Fact]
    public void Compose_AdjacentIdenticalStyles_Merge()
    {
        List<AnsiRun> ansiRuns = [new("a", AnsiStyle.Default), new("b", AnsiStyle.Default)];
        List<HighlightRun> highlightRuns = [new("abc", 1, 2, 3)];

        IReadOnlyList<StyleRun> result = StyledTextComposer.Compose(ansiRuns, highlightRuns);

        StyleRun run = Assert.Single(result);
        Assert.Equal("ab", run.Text);
        Assert.Equal((byte)1, run.ForegroundR);
    }

    [Fact]
    public void Compose_ReverseWithBothColors_Swaps()
    {
        var reversed = new AnsiStyle(255, 0, 0, 0, 0, 255, false, false, true);

        IReadOnlyList<StyleRun> result = StyledTextComposer.Compose([new("rev", reversed)], [PlainHighlightRun("rev")]);

        StyleRun run = Assert.Single(result);
        Assert.False(run.Inverse);
        Assert.True(run.HasForeground);
        Assert.Equal((byte)0, run.ForegroundR);
        Assert.Equal((byte)255, run.ForegroundB);
        Assert.True(run.HasBackground);
        Assert.Equal((byte)255, run.BackgroundR);
        Assert.Equal((byte)0, run.BackgroundB);
    }

    [Fact]
    public void Compose_ReverseWithoutExplicitColors_SetsInverseFlag()
    {
        var reversed = AnsiStyle.Default with { Reverse = true };

        IReadOnlyList<StyleRun> result = StyledTextComposer.Compose([new("inv", reversed)], [PlainHighlightRun("inv")]);

        StyleRun run = Assert.Single(result);
        Assert.True(run.Inverse);
    }

    [Fact]
    public void Compose_UnderlinePropagates()
    {
        var underlined = AnsiStyle.Default with { Underline = true };

        IReadOnlyList<StyleRun> result = StyledTextComposer.Compose([new("u", underlined)], [PlainHighlightRun("u")]);

        StyleRun run = Assert.Single(result);
        Assert.True(run.Underline);
    }
}

