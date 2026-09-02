using DuCom.Core.Parsing;

namespace DuCom.Tests.Parsing;

public sealed class DisplayTextTransformTests
{
    [Fact]
    public void Apply_AllOff_ReturnsOriginalText()
    {
        string text = "a b\tc\r\nd";

        Assert.Same(text, DisplayTextTransform.Apply(text, false, false, false));
    }

    [Fact]
    public void Apply_ShowSpaces_ReplacesSpacesOnly()
    {
        Assert.Equal("a·b\tc", DisplayTextTransform.Apply("a b\tc", false, true, false));
    }

    [Fact]
    public void Apply_ShowTabs_ReplacesTabsOnly()
    {
        Assert.Equal("a b→c", DisplayTextTransform.Apply("a b\tc", false, false, true));
    }

    [Fact]
    public void Apply_ShowControlCharacters_ReplacesCrLf()
    {
        Assert.Equal("a␍␊b", DisplayTextTransform.Apply("a\r\nb", true, false, false));
    }

    [Fact]
    public void Apply_ShowControlCharacters_ReplacesLoneCrAndLf()
    {
        Assert.Equal("a␍b␊c", DisplayTextTransform.Apply("a\rb\nc", true, false, false));
    }

    [Fact]
    public void Apply_AllOn_CombinesSubstitutions()
    {
        Assert.Equal("·a→b␍␊", DisplayTextTransform.Apply(" a\tb\r\n", true, true, true));
    }

    [Fact]
    public void Apply_EmptyText_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, DisplayTextTransform.Apply(string.Empty, true, true, true));
    }

    [Fact]
    public void TimestampsToLocal_ConvertsUtcIsoToLocal()
    {
        DateTimeOffset utc = new(2026, 8, 27, 9, 31, 26, TimeSpan.Zero);
        string result = DisplayTextTransform.TimestampsToLocal("boot at 2026-08-27T09:31:26Z done");

        string expected = utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal($"boot at {expected} done", result);
    }

    [Fact]
    public void TimestampsToLocal_ConvertsUnixMilliseconds()
    {
        long milliseconds = 1_819_479_486_000; // deterministic value
        string result = DisplayTextTransform.TimestampsToLocal($"ts={milliseconds}");

        string expected = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).ToLocalTime()
            .ToString("yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal($"ts={expected}", result);
    }

    [Fact]
    public void TimestampsToLocal_KeepsExplicitOffsets()
    {
        Assert.Equal("at 2026-08-27T09:31:26+08:00 end", DisplayTextTransform.TimestampsToLocal("at 2026-08-27T09:31:26+08:00 end"));
    }

    [Fact]
    public void TimestampsToLocal_NoTokens_ReturnsOriginalText()
    {
        string text = "plain text without timestamps 12345";
        Assert.Equal(text, DisplayTextTransform.TimestampsToLocal(text));
    }

    [Fact]
    public void TimestampsToLocal_IgnoresShortDigitRuns()
    {
        string text = "ids 1234567890 and 12.5";
        Assert.Equal(text, DisplayTextTransform.TimestampsToLocal(text));
    }

    [Fact]
    public void TimestampsToLocal_EmptyText_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, DisplayTextTransform.TimestampsToLocal(string.Empty));
    }
}
