using DuCom.Core.Parsing;

namespace DuCom.Core.Tests.Parsing;

public sealed class DisplayTextSafetyTests
{
    [Fact]
    public void SuspiciousGibberishIsDetected()
    {
        string text = "prefix\uFFFD\u0001\uFFFD\u0090\u4E2D\uE000tail";

        Assert.True(DisplayTextSafety.IsSuspiciousText(text));
    }

    [Fact]
    public void NormalChineseAndAnsiTextAreNotClassifiedAsGibberish()
    {
        Assert.False(DisplayTextSafety.IsSuspiciousText("正常中文日志内容"));
        Assert.False(DisplayTextSafety.IsSuspiciousText("[DTIOT][\u001B[1;31mERR\u001B[m] failed"));
    }

    [Fact]
    public void SmallNumberOfMalformedCharactersDoesNotDiscardValidText()
    {
        string text = "设备日志包含一个异常字符\uFFFD但仍应显示";

        Assert.False(DisplayTextSafety.IsSuspiciousText(text));
    }

    [Theory]
    [InlineData("[13:31:39.781] feed watchdog", true)]
    [InlineData("[13:31:39.781] 正常中文日志输出内容", true)]
    [InlineData("[13:31:39.781] kcc", false)]
    [InlineData("[13:31:39.781] d", false)]
    [InlineData("[13:31:39.781] normal\u0001broken text", false)]
    public void ClearlyReadableTextRequiresARealLogBody(string text, bool expected)
    {
        Assert.Equal(expected, DisplayTextSafety.IsClearlyReadableText(text));
    }
}
