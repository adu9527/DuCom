using System.IO;
using System.Xml.Linq;
using Xunit;

namespace DuCom.App.Tests;

public sealed class AnalysisXamlTests
{
    [Fact]
    public void AnalysisMenuUsesCheckableOneWayBindings()
    {
        XDocument document = XDocument.Load(Path.Combine(SourceRoot(), "MainWindow.xaml"));
        XElement[] items = document.Descendants().Where(element => element.Name.LocalName == "MenuItem")
            .Where(element => element.Attribute("Header")?.Value.Contains("Menu.View.ProtocolDecoder", StringComparison.Ordinal) == true ||
                              element.Attribute("Header")?.Value.Contains("Menu.View.VariablePlot", StringComparison.Ordinal) == true).ToArray();
        Assert.Equal(2, items.Length);
        Assert.All(items, item => Assert.Equal("True", item.Attribute("IsCheckable")?.Value));
        Assert.All(items, item => Assert.Contains("Mode=OneWay", item.Attribute("IsChecked")?.Value));
    }

    [Fact]
    public void EnglishAndChineseLanguageKeysMatch()
    {
        HashSet<string> english = Keys("en-US.xaml");
        HashSet<string> chinese = Keys("zh-CN.xaml");
        Assert.Equal(english.Order(), chinese.Order());
    }

    [Fact]
    public void ProtocolDecoderGridIncludesLocalizedDirectionColumn()
    {
        XDocument document = XDocument.Load(Path.Combine(SourceRoot(), "ProtocolDecoderWindow.xaml"));
        XElement column = Assert.Single(document.Descendants(), element =>
            element.Name.LocalName == "DataGridTextColumn" &&
            element.Attribute("Binding")?.Value.Contains("Direction", StringComparison.Ordinal) == true);
        Assert.Contains("Analysis.Direction", column.Attribute("Header")?.Value, StringComparison.Ordinal);
        Assert.Contains("Analysis.Direction", Keys("en-US.xaml"));
    }

    private static HashSet<string> Keys(string file) => XDocument.Load(Path.Combine(SourceRoot(), "Resources", "Languages", file))
        .Root!.Elements().Select(element => element.Attributes().Single(attribute => attribute.Name.LocalName == "Key").Value).ToHashSet();
    private static string SourceRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DuCom.slnx"))) directory = directory.Parent;
        return Path.Combine(directory?.FullName ?? throw new DirectoryNotFoundException(), "src", "DuCom");
    }
}
