using DuCom.Core.Sending;
using Xunit;

namespace DuCom.Core.Tests.Sending;

public class ReceiveTailTests
{
    [Fact]
    public void FirstShortReplyIsKeptEntirely()
    {
        string tail = ReceiveTail.Append(string.Empty, "OK");

        Assert.Equal("\nOK", tail);
    }

    [Fact]
    public void ExactlyMaxLengthIsKeptEntirely()
    {
        string payload = new('x', ReceiveTail.DefaultMaxLength - 1);

        string tail = ReceiveTail.Append(string.Empty, payload, ReceiveTail.DefaultMaxLength);

        Assert.Equal(ReceiveTail.DefaultMaxLength, tail.Length);
        Assert.Equal($"\n{payload}", tail);
    }

    [Fact]
    public void LongerThanMaxLengthKeepsOnlyTheLastCharacters()
    {
        string payload = new('x', ReceiveTail.DefaultMaxLength + 10);

        string tail = ReceiveTail.Append(string.Empty, payload, ReceiveTail.DefaultMaxLength);

        Assert.Equal(ReceiveTail.DefaultMaxLength, tail.Length);
        Assert.EndsWith(new string('x', 9), tail);
    }

    [Fact]
    public void MultipleAppendsConcatenateWithSeparators()
    {
        string tail = ReceiveTail.Append(string.Empty, "a", 100);
        tail = ReceiveTail.Append(tail, "b", 100);
        tail = ReceiveTail.Append(tail, "c", 100);

        Assert.Equal("\na\nb\nc", tail);
    }

    [Fact]
    public void ExpectedResultSpanningTwoContinuationSegmentsRemainsMatchable()
    {
        const string expected = "RUN DONE";
        string tail = ReceiveTail.Append(string.Empty, "RUN ", 64, ReceiveTail.ContinuationSeparator);
        tail = ReceiveTail.Append(tail, "DONE", 64, ReceiveTail.ContinuationSeparator);

        Assert.Contains(expected, tail);
    }

    [Fact]
    public void DistinctLinesKeepSeparatorAndTrimmingDropsOldestContentOnly()
    {
        int max = 8;
        string tail = ReceiveTail.Append(string.Empty, "1111", max);
        tail = ReceiveTail.Append(tail, "2222", max);

        Assert.Equal(max, tail.Length);
        Assert.EndsWith("2222", tail);
        Assert.DoesNotContain("1111", tail);
    }
}
