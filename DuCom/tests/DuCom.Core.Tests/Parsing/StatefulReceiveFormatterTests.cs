using System.Text;
using DuCom.Core.Parsing;

namespace DuCom.Core.Tests.Parsing;

public sealed class StatefulReceiveFormatterTests
{
    private static readonly DateTimeOffset FirstReceivedAt = new(2026, 8, 26, 1, 2, 3, 456, TimeSpan.Zero);
    private static readonly DateTimeOffset SecondReceivedAt = FirstReceivedAt.AddSeconds(1);

    [Fact]
    public void ContractsExposeRequiredValues()
    {
        Assert.Equal([ReceiveDisplayMode.Str, ReceiveDisplayMode.Hex], Enum.GetValues<ReceiveDisplayMode>());
        Assert.Equal([NewlineKind.CrLf, NewlineKind.Cr, NewlineKind.Lf], Enum.GetValues<NewlineKind>());

        FormattedLine line = new("text", true, FirstReceivedAt);

        Assert.Equal("text", line.Text);
        Assert.True(line.IsTerminated);
        Assert.Equal(FirstReceivedAt, line.ReceivedAtUtc);
    }

    [Fact]
    public void StrFormatsAsciiAndOnlyRealNewlinesTerminateLines()
    {
        StatefulReceiveFormatter formatter = CreateStrFormatter();

        Assert.Equal(
            [new FormattedLine("hello", false, FirstReceivedAt, IsSoftWrapped: true)],
            formatter.Append("hello"u8, FirstReceivedAt));
        Assert.Equal(
            [new FormattedLine(" world", false, FirstReceivedAt, IsSoftWrapped: true)],
            formatter.Append(" world"u8, SecondReceivedAt));
        Assert.Equal(
            [new FormattedLine(string.Empty, true, FirstReceivedAt)],
            formatter.Append("\n"u8, SecondReceivedAt));
    }

    [Fact]
    public void StrDecoderPreservesChineseCharacterSplitAcrossBlocks()
    {
        StatefulReceiveFormatter formatter = CreateStrFormatter();
        byte[] bytes = Encoding.UTF8.GetBytes("中");

        Assert.Empty(formatter.Append(bytes.AsSpan(0, 2), FirstReceivedAt));
        Assert.Equal(
            [new FormattedLine("中", false, FirstReceivedAt, IsSoftWrapped: true)],
            formatter.Append(bytes.AsSpan(2), SecondReceivedAt));
        Assert.Empty(formatter.Flush());
    }

    [Fact]
    public void StrUsesReplacementFallbackForMalformedInput()
    {
        StatefulReceiveFormatter formatter = CreateStrFormatter();

        Assert.Equal(
            [new FormattedLine("�A", false, FirstReceivedAt, IsSoftWrapped: true)],
            formatter.Append([0xff, (byte)'A'], FirstReceivedAt));
        Assert.Empty(formatter.Flush());
    }

    [Fact]
    public void StrRecognizesMixedNewlineForms()
    {
        StatefulReceiveFormatter formatter = CreateStrFormatter();

        Assert.Equal(
            [
                new FormattedLine("a", true, FirstReceivedAt),
                new FormattedLine("b", true, FirstReceivedAt),
                new FormattedLine("c", true, FirstReceivedAt),
                new FormattedLine("d", false, FirstReceivedAt, IsSoftWrapped: true),
            ],
            formatter.Append("a\r\nb\rc\nd"u8, FirstReceivedAt));
    }

    [Fact]
    public void StrRecognizesCrLfAcrossBlocksWithoutCreatingAnExtraLine()
    {
        StatefulReceiveFormatter formatter = CreateStrFormatter();

        Assert.Equal(
            [new FormattedLine("a", false, FirstReceivedAt, IsSoftWrapped: true)],
            formatter.Append("a\r"u8, FirstReceivedAt));
        Assert.Equal(
            [
                new FormattedLine(string.Empty, true, FirstReceivedAt),
                new FormattedLine("b", false, SecondReceivedAt, IsSoftWrapped: true),
            ],
            formatter.Append("\nb"u8, SecondReceivedAt));
    }

    [Fact]
    public void StrMakesNulVisible()
    {
        StatefulReceiveFormatter formatter = CreateStrFormatter();

        Assert.Equal(
            [new FormattedLine("a\\0b", false, FirstReceivedAt, IsSoftWrapped: true)],
            formatter.Append([(byte)'a', 0, (byte)'b'], FirstReceivedAt));
        Assert.Empty(formatter.Flush());
    }

    [Fact]
    public void HexUsesUppercasePairsAndPreservesSpacingAcrossBlocks()
    {
        StatefulReceiveFormatter formatter = new(Encoding.UTF8, ReceiveDisplayMode.Hex, timestampEnabled: false);

        Assert.Equal(
            [new FormattedLine("01 AF", false, FirstReceivedAt, IsSoftWrapped: true)],
            formatter.Append([0x01, 0xaf], FirstReceivedAt));
        Assert.Equal(
            [new FormattedLine(" 00 FF", false, FirstReceivedAt, IsSoftWrapped: true)],
            formatter.Append([0x00, 0xff], SecondReceivedAt));
        Assert.Empty(formatter.Flush());
    }

    [Fact]
    public void TimestampIsAddedOnlyAtTheRealLogicalLineStart()
    {
        StatefulReceiveFormatter formatter = new(Encoding.UTF8, ReceiveDisplayMode.Str, timestampEnabled: true);

        Assert.Equal(
            [new FormattedLine($"[{FirstReceivedAt.ToLocalTime():HH:mm:ss.fff}] a", false, FirstReceivedAt, IsSoftWrapped: true)],
            formatter.Append("a"u8, FirstReceivedAt));
        Assert.Equal(
            [new FormattedLine("b", true, FirstReceivedAt)],
            formatter.Append("b\n"u8, SecondReceivedAt));
        Assert.Equal(
            [new FormattedLine($"[{SecondReceivedAt.ToLocalTime():HH:mm:ss.fff}] c", false, SecondReceivedAt, IsSoftWrapped: true)],
            formatter.Append("c"u8, SecondReceivedAt));
        Assert.Empty(formatter.Flush());

    }

    [Fact]
    public void TimestampPrefixUsesComputerLocalTimeAndConfiguredFormat()
    {
        StatefulReceiveFormatter formatter = new(
            Encoding.UTF8,
            ReceiveDisplayMode.Str,
            timestampEnabled: true,
            timestampFormat: "yyyy-MM-dd HH:mm:ss");

        FormattedLine line = Assert.Single(formatter.Append("a"u8, FirstReceivedAt));

        Assert.Equal($"[{FirstReceivedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}] a", line.Text);
    }

    [Fact]
    public void LongUnterminatedInputIsSoftWrappedWithoutGrowingAnUnboundedSnapshot()
    {
        StatefulReceiveFormatter formatter = new(Encoding.UTF8, ReceiveDisplayMode.Str, false, maximumLineCharacters: 4);

        Assert.Equal(
            [
                new FormattedLine("abcd", false, FirstReceivedAt, IsSoftWrapped: true),
                new FormattedLine("efgh", false, FirstReceivedAt, IsSoftWrapped: true),
                new FormattedLine("ij", false, FirstReceivedAt, IsSoftWrapped: true),
            ],
            formatter.Append("abcdefghij"u8, FirstReceivedAt));
        Assert.Empty(formatter.Flush());
    }

    [Fact]
    public void SoftWrapDoesNotSplitSurrogatePair()
    {
        StatefulReceiveFormatter formatter = new(Encoding.UTF8, ReceiveDisplayMode.Str, false, maximumLineCharacters: 2);

        IReadOnlyList<FormattedLine> lines = formatter.Append(Encoding.UTF8.GetBytes("a😀b"), FirstReceivedAt);
        IReadOnlyList<FormattedLine> flushed = formatter.Flush();

        Assert.Equal("a😀b", string.Concat(lines.Concat(flushed).Select(line => line.Text)));
        Assert.DoesNotContain(lines.Concat(flushed), line => line.Text.Contains('�'));
    }

    [Fact]
    public void FlushOutputsUnterminatedStrLineAndCompletesPendingDecoderInput()
    {
        StatefulReceiveFormatter formatter = CreateStrFormatter();

        Assert.Empty(formatter.Append([0xe4, 0xb8], FirstReceivedAt));

        Assert.Equal(
            [new FormattedLine("�", false, FirstReceivedAt)],
            formatter.Flush());
        Assert.Empty(formatter.Flush());
    }

    [Fact]
    public void FlushUsesLastValidReceiveTimeForTrailingIncompleteMultibyteInput()
    {
        StatefulReceiveFormatter formatter = new(Encoding.UTF8, ReceiveDisplayMode.Str, timestampEnabled: true);
        byte[] prefix = Encoding.UTF8.GetBytes("a\n");
        byte[] bytes = [.. prefix, 0xe4, 0xb8];

        _ = formatter.Append(bytes, FirstReceivedAt);
        FormattedLine flushed = Assert.Single(formatter.Flush());

        Assert.Equal(FirstReceivedAt, flushed.ReceivedAtUtc);
        Assert.StartsWith($"[{FirstReceivedAt.ToLocalTime():HH:mm:ss.fff}]", flushed.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("[00:00:00.000]", flushed.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void FlushCompletesPendingCrAsARealNewline()
    {
        StatefulReceiveFormatter formatter = CreateStrFormatter();

        formatter.Append("a\r"u8, FirstReceivedAt);

        Assert.Equal([new FormattedLine(string.Empty, true, FirstReceivedAt)], formatter.Flush());
    }

    [Fact]
    public void FlushOutputsUnterminatedHexLine()
    {
        StatefulReceiveFormatter formatter = new(Encoding.UTF8, ReceiveDisplayMode.Hex, timestampEnabled: false);
        formatter.Append([0x0a, 0xbc], FirstReceivedAt);

        Assert.Empty(formatter.Flush());
        Assert.Empty(formatter.Flush());
    }

    private static StatefulReceiveFormatter CreateStrFormatter() =>
        new(Encoding.UTF8, ReceiveDisplayMode.Str, timestampEnabled: false);
}
