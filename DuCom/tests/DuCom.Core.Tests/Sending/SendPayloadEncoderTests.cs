using System.Text;
using DuCom.Core.Sending;

namespace DuCom.Core.Tests.Sending;

public sealed class SendPayloadEncoderTests
{
    [Theory]
    [InlineData(NewlinePolicy.None, "41")]
    [InlineData(NewlinePolicy.Cr, "410D")]
    [InlineData(NewlinePolicy.Lf, "410A")]
    [InlineData(NewlinePolicy.CrLf, "410D0A")]
    public void StringModeAppliesExplicitNewlineBytes(NewlinePolicy newline, string expectedHex)
    {
        byte[] payload = SendPayloadEncoder.EncodeString("A", Encoding.UTF8, newline);
        Assert.Equal(expectedHex, Convert.ToHexString(payload));
    }

    [Fact]
    public void HexModeParsesWhitespaceAndAppliesNewlineBytes()
    {
        byte[] payload = SendPayloadEncoder.EncodeHex("01 af 00", NewlinePolicy.CrLf);
        Assert.Equal("01AF000D0A", Convert.ToHexString(payload));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("GG")]
    public void InvalidHexIsRejected(string text) =>
        Assert.Throws<FormatException>(() => SendPayloadEncoder.EncodeHex(text, NewlinePolicy.None));
}
