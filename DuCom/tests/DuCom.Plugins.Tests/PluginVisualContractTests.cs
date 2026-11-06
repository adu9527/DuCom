using System.Xml.Linq;
using Xunit;

namespace DuCom.Plugins.Tests;

/// <summary>Source-level checks deliberately avoid WPF Application/dispatcher global state.</summary>
public sealed class PluginVisualContractTests
{
    [Fact]
    public void SharedPluginStylesUseDynamicThemeAndPreserveNativeInputParts()
    {
        XDocument styles = XDocument.Load(Path.Combine(PluginDirectory(), "PluginUiStyles.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        Assert.Contains(styles.Descendants(), element => (string?)element.Attribute(x + "Key") == "PluginUi.PrimaryButton");
        Assert.Contains(styles.Descendants(), element => (string?)element.Attribute("Value") == "{DynamicResource Brush.AccentFill}");
        Assert.Contains(styles.Descendants(), element => (string?)element.Attribute("Value") == "{DynamicResource Brush.AccentFillHover}");
        Assert.Contains(styles.Descendants(), element => (string?)element.Attribute("Value") == "{DynamicResource Brush.AccentFillPressed}");
        Assert.Contains(styles.Descendants(), element => (string?)element.Attribute(x + "Name") == "PART_ContentHost");
        Assert.Contains(styles.Descendants(), element => (string?)element.Attribute(x + "Name") == "PART_Popup");
        Assert.Contains(styles.Descendants(), element => (string?)element.Attribute("Property") == "IsKeyboardFocused");
        Assert.Contains(styles.Descendants(), element => (string?)element.Attribute("Property") == "IsEnabled");
        Assert.DoesNotContain(styles.Descendants(), element => element.Name.LocalName.Contains("Animation", StringComparison.Ordinal));
        Assert.DoesNotContain(styles.Descendants(), element => (string?)element.Attribute("Value") == "Bold");
    }

    [Theory]
    [InlineData("ChecksumWindow.xaml", "OpenButton", "Open_Click")]
    [InlineData("CrcWindow.xaml", "CalculateButton", "Calculate_Click")]
    [InlineData("ProductionWindow.xaml", "StartButton", "Start_Click")]
    [InlineData("AudioWindow.xaml", "GenerateButton", "Generate_Click")]
    public void PluginWindowRetainsPrimaryCommandAndUsesSharedVisualContract(string file, string name, string handler)
    {
        XDocument document = XDocument.Load(Path.Combine(PluginDirectory(), file));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement action = document.Descendants().Single(element => (string?)element.Attribute(x + "Name") == name);
        Assert.Equal(handler, (string?)action.Attribute("Click"));
        Assert.DoesNotContain(document.Descendants(), element => (string?)element.Attribute("FontWeight") == "Bold");
        Assert.DoesNotContain(document.Descendants(), element => (string?)element.Attribute("SnapsToDevicePixels") == "True");
        Assert.DoesNotContain(document.Descendants(), element => (string?)element.Attribute("TextOptions.TextRenderingMode") == "ClearType");
        if (file != "AudioWindow.xaml")
            Assert.Contains(document.Descendants().Attributes(), attribute => attribute.Value.Contains("{DynamicResource Brush.", StringComparison.Ordinal));
    }

    [Fact]
    public void BesDensityIsScopedAndUsesReadableCompactDimensions()
    {
        XDocument styles = XDocument.Load(Path.Combine(PluginDirectory(), "BesCompactStyles.xaml"));
        Assert.Contains(styles.Descendants(), element => (string?)element.Attribute("Property") == "FontSize" && (string?)element.Attribute("Value") == "13");
        Assert.Contains(styles.Descendants(), element => (string?)element.Attribute("Width") == "16" && (string?)element.Attribute("Height") == "16");
        Assert.DoesNotContain(styles.Descendants(), element => (string?)element.Attribute("Width") == "21");
        string adapter = File.ReadAllText(Path.Combine(PluginDirectory(), "BesCompactPresentation.cs"));
        Assert.Contains("internal.ducom.bes-download", adapter, StringComparison.Ordinal);
        Assert.Contains("LogicalTreeHelper.GetParent", adapter, StringComparison.Ordinal);
        Assert.DoesNotContain("IsEnabled =", adapter, StringComparison.Ordinal);
        Assert.DoesNotContain("IsChecked =", adapter, StringComparison.Ordinal);
    }

    private static string PluginDirectory()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "src", "DuCom", "Services", "Plugins");
            if (Directory.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Plugin UI sources were not found above the test output directory.");
    }
}
