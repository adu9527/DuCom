using System.IO;
using System.Xml.Linq;
using Xunit;

namespace DuCom.App.Tests;

public sealed class UiConsistencyTests
{
    [Fact]
    public void PluginManagerKeepsAllLifecycleAndRecoveryActions()
    {
        XDocument document = XDocument.Load(Path.Combine(SourceRoot(), "PluginManagerControl.xaml"));
        string[] commands = document.Descendants().Attributes("Command").Select(a => a.Value).ToArray();
        foreach (string command in new[] { "EnablePlugin", "DisablePlugin", "RestartPlugin", "OpenPlugin", "UninstallSelected",
            "EnableAllPlugins", "DisableAllPlugins", "InstallPlugin", "ApplyAndRestart", "OpenPluginFolder", "RefreshPlugins" })
            Assert.Contains($"{{Binding {command}Command}}", commands);
        Assert.Contains(document.Descendants().Attributes("Text"), a => a.Value == "{Binding OperationMessage}");
        Assert.Contains(document.Descendants().Attributes("IsChecked"), a => a.Value.Contains("SafeStartAllPlugins", StringComparison.Ordinal));
        foreach (XElement button in document.Descendants().Where(e => e.Name.LocalName == "Button"))
            Assert.NotNull(button.Attribute("Command"));
    }

    [Fact]
    public void EveryThemeProvidesTheSameTokensAndAccessibleActionColors()
    {
        var dark = Tokens("Dark");
        var light = Tokens("Light");
        Assert.Equal(dark.Keys.Order(), light.Keys.Order());
        foreach (var palette in new[] { dark, light })
        {
            Assert.True(Contrast("#FFFFFF", palette["Brush.AccentFill"]) >= 4.5);
            foreach (string surface in new[] { "Brush.PanelSurface", "Brush.PanelRaised", "Brush.ShellSurface" })
                Assert.True(Contrast(palette["Brush.TextMuted"], palette[surface]) >= 4.5,
                    $"Muted text lacks contrast on {surface}");
        }
    }

    [Fact]
    public void FluentAndStandardWindowsShareRenderingPolicy()
    {
        XDocument document = XDocument.Load(Path.Combine(SourceRoot(), "App.xaml"));
        // Window/FluentWindow must retain the library's implicit theme style/template.
        Assert.DoesNotContain(document.Descendants(), e => e.Name.LocalName == "Style" &&
            e.Attribute("TargetType")?.Value is "Window" or "ui:FluentWindow" && !e.Attributes().Any(a => a.Name.LocalName == "Key"));
        Assert.Contains(document.Descendants(), e => e.Attribute("Property")?.Value == "TextOptions.TextFormattingMode" && e.Attribute("Value")?.Value == "Display");
        Assert.DoesNotContain(document.Descendants(), e => e.Attribute("Value")?.Value == "ClearType");
    }

    [Fact]
    public void SettingsKeepCategoryOrderAndNewlineRadioSemantics()
    {
        XDocument document = XDocument.Load(Path.Combine(SourceRoot(), "SettingsWindow.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement categories = document.Descendants().Single(e => e.Attribute(x + "Name")?.Value == "CategoryList");
        Assert.Equal(10, categories.Elements().Count(e => e.Name.LocalName == "ListBoxItem"));
        foreach (string group in new[] { "DefaultNewline", "TransportNewline" })
            Assert.Equal(4, document.Descendants().Count(e => e.Name.LocalName == "RadioButton" && e.Attribute("GroupName")?.Value == group));
    }

    private static Dictionary<string, string> Tokens(string theme)
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        return XDocument.Load(Path.Combine(SourceRoot(), "Resources", $"DesignTokens.Colors.{theme}.xaml"))
            .Root!.Elements().ToDictionary(e => e.Attribute(x + "Key")!.Value, e => e.Attribute("Color")!.Value);
    }
    private static double Contrast(string a, string b)
    {
        double Luminance(string hex)
        {
            hex = hex[^6..];
            double Component(int offset)
            {
                double v = Convert.ToInt32(hex.Substring(offset, 2), 16) / 255d;
                return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Component(0) + 0.7152 * Component(2) + 0.0722 * Component(4);
        }
        double l1 = Luminance(a), l2 = Luminance(b);
        return (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);
    }
    private static string SourceRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DuCom.slnx"))) directory = directory.Parent;
        return Path.Combine(directory?.FullName ?? throw new DirectoryNotFoundException(), "src", "DuCom");
    }
}
