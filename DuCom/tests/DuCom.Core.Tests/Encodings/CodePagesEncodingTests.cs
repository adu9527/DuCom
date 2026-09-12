using System.Text;
using DuCom.Core.Parsing;
using DuCom.Core.Ports;

namespace DuCom.Core.Tests.Encodings;

public sealed class CodePagesEncodingTests
{
    private static readonly DateTimeOffset FirstReceivedAt = new(2026, 8, 26, 1, 2, 3, 456, TimeSpan.Zero);
    private static readonly DateTimeOffset SecondReceivedAt = FirstReceivedAt.AddSeconds(1);

    [Fact]
    public void GbkAndGb2312EncodingsAreAvailable()
    {
        // Touch a DuCom.Core type first so its module initializer (the CodePages
        // provider registration) is guaranteed to have run in this process.
        _ = SerialPortSettings.Default("COM1").EncodingName;

        Assert.NotNull(Encoding.GetEncoding("gbk"));
        Assert.NotNull(Encoding.GetEncoding("gb2312"));
    }

    [Fact]
    public void SerialPortSettingsValidateAcceptsGbkAndGb2312()
    {
        SerialPortSettings gbk = SerialPortSettings.Default("COM3") with { EncodingName = "gbk" };
        gbk.Validate();

        SerialPortSettings gb2312 = SerialPortSettings.Default("COM3") with { EncodingName = "gb2312" };
        gb2312.Validate();
    }

    [Fact]
    public void ReceiveFormattingProfileAcceptsGbkAndGb2312()
    {
        ReceiveFormattingProfile gbk = new(
            Version: 1,
            EncodingName: "gbk",
            DisplayMode: ReceiveDisplayMode.Str,
            TimestampEnabled: false);
        gbk.Validate();
        gbk.CreateFormatter();

        ReceiveFormattingProfile gb2312 = new(
            Version: 1,
            EncodingName: "gb2312",
            DisplayMode: ReceiveDisplayMode.Str,
            TimestampEnabled: false);
        gb2312.Validate();
        gb2312.CreateFormatter();
    }

    [Fact]
    public void GbkRoundTripEncodesYouHaoToExpectedBytes()
    {
        Encoding gbk = Encoding.GetEncoding("gbk");
        byte[] expected = [0xC4, 0xE3, 0xBA, 0xC3];

        byte[] encoded = gbk.GetBytes("你好");

        Assert.Equal(expected, encoded);
        Assert.Equal("你好", gbk.GetString(encoded));
    }

    [Fact]
    public void GbkDecoderPreservesChineseCharacterSplitAcrossBlocks()
    {
        StatefulReceiveFormatter formatter = CreateGbkStrFormatter();
        byte[] bytes = Encoding.GetEncoding("gbk").GetBytes("中");

        Assert.Empty(formatter.Append(bytes.AsSpan(0, 1), FirstReceivedAt));
        Assert.Equal(
            [new FormattedLine("中", false, FirstReceivedAt, IsSoftWrapped: true)],
            formatter.Append(bytes.AsSpan(1), SecondReceivedAt));
        Assert.Empty(formatter.Flush());
    }

    [Fact]
    public void GbkUsesReplacementFallbackForMalformedInput()
    {
        // 0xFF is NOT malformed in CP936: it maps to private-use U+F8F5. A malformed
        // sequence must be a valid lead byte followed by an invalid trail byte (trails
        // are 0x40-0xFE); 0xD6 is the lead byte of "中", space is an invalid trail.
        // An invalid two-byte sequence is consumed whole and yields one replacement.
        StatefulReceiveFormatter formatter = CreateGbkStrFormatter();

        Assert.Equal(
            [new FormattedLine("\uFFFD", false, FirstReceivedAt, IsSoftWrapped: true)],
            formatter.Append([0xD6, (byte)' '], FirstReceivedAt));
        Assert.Empty(formatter.Flush());
    }

    private static StatefulReceiveFormatter CreateGbkStrFormatter() =>
        new ReceiveFormattingProfile(
            Version: 1,
            EncodingName: "gbk",
            DisplayMode: ReceiveDisplayMode.Str,
            TimestampEnabled: false)
            .CreateFormatter();
}
