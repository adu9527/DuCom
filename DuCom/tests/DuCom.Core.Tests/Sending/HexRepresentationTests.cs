using DuCom.Core.Sending;

namespace DuCom.Core.Tests.Sending;

public sealed class HexRepresentationTests
{
    [Theory]
    [InlineData(new byte[] { }, "")]
    [InlineData(new byte[] { 0x0A }, "0A")]
    [InlineData(new byte[] { 0xAB, 0xCD, 0xEF }, "AB CD EF")]
    [InlineData(new byte[] { 0x00, 0xFF, 0x10 }, "00 FF 10")]
    public void ToHexText_FormatsUppercaseSpaceSeparated(byte[] data, string expected)
    {
        Assert.Equal(expected, HexRepresentation.ToHexText(data));
    }

    [Theory]
    [InlineData("", true, new byte[] { })]
    [InlineData("AB CD EF", true, new byte[] { 0xAB, 0xCD, 0xEF })]
    [InlineData("ab cd", true, new byte[] { 0xAB, 0xCD })]
    [InlineData("00 FF 10", true, new byte[] { 0x00, 0xFF, 0x10 })]
    [InlineData("ABC", false, new byte[] { })]
    [InlineData("AB C", false, new byte[] { })]
    [InlineData("AB-CD", false, new byte[] { })]
    [InlineData("AB  GG", false, new byte[] { })]
    public void TryParseHexText_ValidatesAndParses(string text, bool expectedValid, byte[] expectedBytes)
    {
        bool valid = HexRepresentation.TryParseHexText(text, out byte[] bytes);

        Assert.Equal(expectedValid, valid);
        if (expectedValid)
        {
            Assert.Equal(expectedBytes, bytes);
        }
        else
        {
            Assert.Empty(bytes);
        }
    }

    [Fact]
    public void RoundTrip_TextToHexAndBack()
    {
        byte[] payload = [0x01, 0x2B, 0x3C, 0xFF];

        string hex = HexRepresentation.ToHexText(payload);
        bool parsed = HexRepresentation.TryParseHexText(hex, out byte[] bytes);

        Assert.True(parsed);
        Assert.Equal(payload, bytes);
    }
}
