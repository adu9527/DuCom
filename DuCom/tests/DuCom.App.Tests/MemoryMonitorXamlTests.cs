using System.IO;
using System.Xml.Linq;
using Xunit;

namespace DuCom.App.Tests;

public sealed class MemoryMonitorXamlTests
{
    [Theory]
    [InlineData("en-US", "Total memory", "Main process: {0}\nPlugins: {1}")]
    [InlineData("zh-CN", "总内存", "主进程：{0}\n插件：{1}")]
    public void TitleBarUsesLocalizedTotalAndTwoLineBreakdownTooltip(string language, string label, string tooltip)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DuCom.slnx")))
            directory = directory.Parent;
        string source = Path.Combine(directory!.FullName, "src", "DuCom");
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XDocument resources = XDocument.Load(Path.Combine(source, "Resources", "Languages", language + ".xaml"));
        string Resource(string key) => resources.Root!.Elements().Single(element => (string?)element.Attribute(x + "Key") == key).Value;
        Assert.Equal(label, Resource("MemoryMonitor.TotalLabel"));
        Assert.Equal(tooltip, Resource("MemoryMonitor.ProcessTooltip"));
        Assert.NotEmpty(Resource("MemoryMonitor.Unavailable"));
        XDocument window = XDocument.Load(Path.Combine(source, "MainWindow.xaml"));
        Assert.Contains(window.Descendants(), element => (string?)element.Attribute("Text") == "{DynamicResource MemoryMonitor.TotalLabel}");
    }
}
