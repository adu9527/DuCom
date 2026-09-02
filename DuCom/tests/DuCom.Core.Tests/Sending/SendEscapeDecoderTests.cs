using DuCom.Core.Sending;

namespace DuCom.Core.Tests.Sending;

public sealed class SendEscapeDecoderTests
{
    [Theory]
    [InlineData(@"a\r\nb", "a\r\nb")]
    [InlineData(@"a\tb", "a\tb")]
    [InlineData(@"a\\b", @"a\b")]
    [InlineData(@"A\x42", "AB")]
    [InlineData(@"A\q", @"A\q")]
    public void DecodeHandlesSupportedEscapes(string input, string expected) =>
        Assert.Equal(expected, SendEscapeDecoder.Decode(input));
}
