using DuCom.Core.Updates;

namespace DuCom.Core.Tests.Updates;

public sealed class ReleaseNotesSanitizerTests
{
    [Fact]
    public void HtmlImageTagsAreRemoved()
    {
        string input = "本次更新：\r\n<img width=\"1281\" height=\"1123\" alt=\"screen\" src=\"https://github.com/user-attachments/assets/29e6e5f4\" />\r\n修复若干问题。";

        string result = ReleaseNotesSanitizer.ToPlainText(input);

        Assert.Equal("本次更新：\n修复若干问题。", result);
    }

    [Fact]
    public void MarkdownImagesAreRemovedButAltTextIsNotKept()
    {
        string input = "说明\r\n![截图](https://example.com/a.png)\r\n正文";

        string result = ReleaseNotesSanitizer.ToPlainText(input);

        Assert.Equal("说明\n正文", result);
    }

    [Fact]
    public void MarkdownLinksKeepOnlyTheText()
    {
        string input = "参见 [发布页](https://github.com/adu9527/DuCom/releases) 了解详情。";

        string result = ReleaseNotesSanitizer.ToPlainText(input);

        Assert.Equal("参见 发布页 了解详情。", result);
    }

    [Fact]
    public void HeadingsListsAndEmphasisAreStripped()
    {
        string input = "## V0.0.0.4 更新\n\n- **新增** 自动更新\n- `修复` 串口问题\n> 备注\n---\n完成";

        string result = ReleaseNotesSanitizer.ToPlainText(input);

        Assert.Equal("V0.0.0.4 更新\n\n新增 自动更新\n修复 串口问题\n备注\n完成", result);
    }

    [Fact]
    public void NumberedListMarkersAreStripped()
    {
        string input = "1. 第一项\n2. 第二项";

        string result = ReleaseNotesSanitizer.ToPlainText(input);

        Assert.Equal("第一项\n第二项", result);
    }

    [Fact]
    public void HtmlEntitiesAreDecoded()
    {
        string input = "A &amp; B &lt;C&gt; &quot;D&quot;";

        string result = ReleaseNotesSanitizer.ToPlainText(input);

        Assert.Equal("A & B <C> \"D\"", result);
    }

    [Fact]
    public void ExcessBlankLinesAreCollapsed()
    {
        string input = "一\n\n\n\n\n二";

        string result = ReleaseNotesSanitizer.ToPlainText(input);

        Assert.Equal("一\n\n二", result);
    }

    [Fact]
    public void EmptyAndNullInputReturnEmptyString()
    {
        Assert.Equal(string.Empty, ReleaseNotesSanitizer.ToPlainText(null));
        Assert.Equal(string.Empty, ReleaseNotesSanitizer.ToPlainText(string.Empty));
        Assert.Equal(string.Empty, ReleaseNotesSanitizer.ToPlainText("   \r\n  "));
    }
}
