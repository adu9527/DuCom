using DuCom.Core.Parsing;

namespace DuCom.Core.Tests.Parsing;

public class AnsiParserTests
{
    [Fact]
    public void Parse_EmptyInput_ReturnsEmpty()
    {
        var parser = new AnsiParser();
        IReadOnlyList<AnsiRun> runs = parser.Parse(string.Empty);
        Assert.Empty(runs);
    }

    [Fact]
    public void Parse_PlainText_ReturnsSingleRunWithDefaultStyle()
    {
        var parser = new AnsiParser();
        IReadOnlyList<AnsiRun> runs = parser.Parse("hello world");
        Assert.Single(runs);
        Assert.Equal("hello world", runs[0].Text);
        Assert.Equal(AnsiStyle.Default, runs[0].Style);
    }

    [Theory]
    [InlineData("\u001B[31mred", "red", 0xCC, 0x40, 0x40)]
    [InlineData("\u001B[32mgreen", "green", 0x4E, 0x9A, 0x06)]
    [InlineData("\u001B[34mblue", "blue", 0x34, 0x65, 0xA4)]
    [InlineData("\u001B[91mbright red", "bright red", 0xEF, 0x29, 0x29)]
    public void Parse_ForegroundColor_SetsForeground(string input, string expectedText, byte r, byte g, byte b)
    {
        var parser = new AnsiParser();
        IReadOnlyList<AnsiRun> runs = parser.Parse(input);
        Assert.Single(runs);
        Assert.Equal(expectedText, runs[0].Text);
        Assert.True(runs[0].Style.HasForeground);
        Assert.Equal(r, runs[0].Style.ForegroundR);
        Assert.Equal(g, runs[0].Style.ForegroundG);
        Assert.Equal(b, runs[0].Style.ForegroundB);
    }

    [Theory]
    [InlineData("\u001B[41mred bg", 0xCC, 0x40, 0x40)]
    [InlineData("\u001B[44mblue bg", 0x34, 0x65, 0xA4)]
    public void Parse_BackgroundColor_SetsBackground(string input, byte r, byte g, byte b)
    {
        var parser = new AnsiParser();
        IReadOnlyList<AnsiRun> runs = parser.Parse(input);
        Assert.Single(runs);
        Assert.True(runs[0].Style.HasBackground);
        Assert.Equal(r, runs[0].Style.BackgroundR);
        Assert.Equal(g, runs[0].Style.BackgroundG);
        Assert.Equal(b, runs[0].Style.BackgroundB);
    }

    [Fact]
    public void Parse_Reset_ClearsStyle()
    {
        var parser = new AnsiParser();
        IReadOnlyList<AnsiRun> runs = parser.Parse("\u001B[31mred\u001B[0mplain");
        Assert.Equal(2, runs.Count);
        Assert.Equal("red", runs[0].Text);
        Assert.True(runs[0].Style.HasForeground);
        Assert.Equal("plain", runs[1].Text);
        Assert.False(runs[1].Style.HasForeground);
        Assert.False(runs[1].Style.Bold);
    }

    [Fact]
    public void Parse_Bold_SetsBoldFlag()
    {
        var parser = new AnsiParser();
        IReadOnlyList<AnsiRun> runs = parser.Parse("\u001B[1mbold\u001B[22mnormal");
        Assert.Equal(2, runs.Count);
        Assert.True(runs[0].Style.Bold);
        Assert.False(runs[1].Style.Bold);
    }

    [Fact]
    public void Parse_Underline_SetsUnderlineFlag()
    {
        var parser = new AnsiParser();
        IReadOnlyList<AnsiRun> runs = parser.Parse("\u001B[4munder\u001B[24mplain");
        Assert.Equal(2, runs.Count);
        Assert.True(runs[0].Style.Underline);
        Assert.False(runs[1].Style.Underline);
    }

    [Fact]
    public void Parse_Reverse_SetsReverseFlag()
    {
        var parser = new AnsiParser();
        IReadOnlyList<AnsiRun> runs = parser.Parse("\u001B[7mrev\u001B[27mplain");
        Assert.Equal(2, runs.Count);
        Assert.True(runs[0].Style.Reverse);
        Assert.False(runs[1].Style.Reverse);
    }

    [Theory]
    [InlineData("\u001B[38;5;196m256 red", 0xFF, 0x00, 0x00)]
    [InlineData("\u001B[38;5;21m256 blue", 0x00, 0x00, 0xFF)]
    public void Parse_256Foreground_SetsColor(string input, byte r, byte g, byte b)
    {
        var parser = new AnsiParser();
        IReadOnlyList<AnsiRun> runs = parser.Parse(input);
        Assert.Single(runs);
        Assert.Equal(r, runs[0].Style.ForegroundR);
        Assert.Equal(g, runs[0].Style.ForegroundG);
        Assert.Equal(b, runs[0].Style.ForegroundB);
    }

    [Fact]
    public void Parse_RgbForeground_SetsExactColor()
    {
        var parser = new AnsiParser();
        IReadOnlyList<AnsiRun> runs = parser.Parse("\u001B[38;2;255;128;64mrgb");
        Assert.Single(runs);
        Assert.Equal((byte)255, runs[0].Style.ForegroundR!.Value);
        Assert.Equal((byte)128, runs[0].Style.ForegroundG!.Value);
        Assert.Equal((byte)64, runs[0].Style.ForegroundB!.Value);
    }

    [Fact]
    public void Parse_RgbBackground_SetsExactColor()
    {
        var parser = new AnsiParser();
        IReadOnlyList<AnsiRun> runs = parser.Parse("\u001B[48;2;10;20;30mbg");
        Assert.Single(runs);
        Assert.Equal((byte)10, runs[0].Style.BackgroundR!.Value);
        Assert.Equal((byte)20, runs[0].Style.BackgroundG!.Value);
        Assert.Equal((byte)30, runs[0].Style.BackgroundB!.Value);
    }

    [Fact]
    public void Parse_MultipleSgrParameters_AppliesAll()
    {
        var parser = new AnsiParser();
        IReadOnlyList<AnsiRun> runs = parser.Parse("\u001B[1;31;42mbold red on green");
        Assert.Single(runs);
        Assert.True(runs[0].Style.Bold);
        Assert.True(runs[0].Style.HasForeground);
        Assert.True(runs[0].Style.HasBackground);
    }

    [Fact]
    public void Parse_TruncatedCsiAcrossBlocks_PreservesState()
    {
        var parser = new AnsiParser();
        IReadOnlyList<AnsiRun> first = parser.Parse("hello \u001B[");
        Assert.Single(first);
        Assert.Equal("hello ", first[0].Text);

        IReadOnlyList<AnsiRun> second = parser.Parse("31mworld");
        Assert.Single(second);
        Assert.Equal("world", second[0].Text);
        Assert.True(second[0].Style.HasForeground);
    }

    [Fact]
    public void Parse_TruncatedExtendedColorAcrossBlocks_PreservesState()
    {
        var parser = new AnsiParser();
        _ = parser.Parse("\u001B[38;2;");
        IReadOnlyList<AnsiRun> second = parser.Parse("255;128;64mtext");
        Assert.Single(second);
        Assert.Equal("text", second[0].Text);
        Assert.Equal((byte)255, second[0].Style.ForegroundR!.Value);
        Assert.Equal((byte)128, second[0].Style.ForegroundG!.Value);
        Assert.Equal((byte)64, second[0].Style.ForegroundB!.Value);
    }

    [Fact]
    public void Parse_MalformedSequence_DoesNotThrow()
    {
        var parser = new AnsiParser();
        IReadOnlyList<AnsiRun> runs = parser.Parse("\u001B[;;mtext\u001B[38;xmixed");
        Assert.Contains(runs, run => run.Text.Contains("text", StringComparison.Ordinal));
        Assert.Contains(runs, run => run.Text.Contains("mixed", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_NonSgrCsi_IsIgnored()
    {
        var parser = new AnsiParser();
        IReadOnlyList<AnsiRun> runs = parser.Parse("\u001B[2Jplain");
        Assert.Single(runs);
        Assert.Equal("plain", runs[0].Text);
        Assert.Equal(AnsiStyle.Default, runs[0].Style);
    }

    [Fact]
    public void Parse_OscSequence_IsIgnored()
    {
        var parser = new AnsiParser();
        IReadOnlyList<AnsiRun> runs = parser.Parse("\u001B]0;title\u0007plain");
        Assert.Single(runs);
        Assert.Equal("plain", runs[0].Text);
    }

    [Fact]
    public void Parse_SoftWrapSegmentBoundary_SplitsRunsCorrectly()
    {
        var parser = new AnsiParser();
        IReadOnlyList<AnsiRun> first = parser.Parse("\u001B[31mred");
        IReadOnlyList<AnsiRun> second = parser.Parse("still red\u001B[0mplain");
        Assert.Single(first);
        Assert.True(first[0].Style.HasForeground);
        Assert.Contains(second, run => run.Text.Contains("still red", StringComparison.Ordinal) && run.Style.HasForeground);
        Assert.Contains(second, run => run.Text.Contains("plain", StringComparison.Ordinal) && !run.Style.HasForeground);
    }

    [Fact]
    public void Flush_AfterIncompleteSequence_ClearsStateAndAllowsContinuedParsing()
    {
        var parser = new AnsiParser();
        IReadOnlyList<AnsiRun> first = parser.Parse("text\u001B[31");
        Assert.Single(first);
        Assert.Equal("text", first[0].Text);
        Assert.False(first[0].Style.HasForeground);

        IReadOnlyList<AnsiRun> flushed = parser.Flush();
        Assert.Empty(flushed);

        IReadOnlyList<AnsiRun> continued = parser.Parse("plain");
        Assert.Single(continued);
        Assert.Equal("plain", continued[0].Text);
        Assert.False(continued[0].Style.HasForeground);
    }

    [Fact]
    public void Reset_ClearsStyleAndState()
    {
        var parser = new AnsiParser();
        _ = parser.Parse("\u001B[31mred");
        parser.Reset();
        IReadOnlyList<AnsiRun> runs = parser.Parse("plain");
        Assert.Single(runs);
        Assert.Equal("plain", runs[0].Text);
        Assert.False(runs[0].Style.HasForeground);
    }
}
