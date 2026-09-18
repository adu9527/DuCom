using DuCom.Core.Parsing;

namespace DuCom.Core.Tests.Parsing;

public sealed class AnsiTextSanitizerTests
{
    [Fact]
    public void RemovesSgrSequencesAndPreservesText()
    {
        AnsiTextSanitizer sanitizer = new();

        string result = sanitizer.Sanitize("[DTIOT][\u001B[1;31mERR\u001B[m] write failed\r\n");

        Assert.Equal("[DTIOT][ERR] write failed\r\n", result);
    }

    [Fact]
    public void RemovesSequencesSplitAcrossWrites()
    {
        AnsiTextSanitizer sanitizer = new();

        Assert.Equal("head", sanitizer.Sanitize("head\u001B[1;"));
        Assert.Equal("ERR tail", sanitizer.Sanitize("31mERR\u001B[m tail"));
    }

    [Fact]
    public void RemovesOscSequencesWithBothTerminators()
    {
        AnsiTextSanitizer sanitizer = new();

        Assert.Equal("ab", sanitizer.Sanitize("a\u001B]0;title\u0007b"));
        Assert.Equal("cd", sanitizer.Sanitize("c\u001B]0;title\u001B\\d"));
    }

    [Fact]
    public void OverlongSequenceRecoversForFollowingText()
    {
        AnsiTextSanitizer sanitizer = new();

        Assert.Equal(string.Empty, sanitizer.Sanitize("\u001B]" + new string('x', 1_024)));
        Assert.Equal("plain", sanitizer.Sanitize("plain"));
    }
}
