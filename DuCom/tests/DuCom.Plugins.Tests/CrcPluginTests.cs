using System.Collections.Concurrent;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using DuCom.Plugin;
using DuCom.PluginHost;
using DuCom.PluginHost.Core;
using DuCom.PluginHost.Packages;
using DuCom.PluginHost.Registry;
using DuCom.Plugins.Crc;
using DuCom.Services.Plugins;
using Xunit;

namespace DuCom.Plugins.Tests;

[Collection("WorkerLifecycle")]
public sealed class CrcPluginTests
{
    [Fact]
    public void AllModelsMatchPublished123456789CheckValues()
    {
        uint[] expected = [0x7, 0x00, 0x07, 0x19, 0x06, 0x75, 0xF4, 0xA1, 0xD0, 0xA1,
            0xBB3D, 0x44C2, 0xB4C8, 0x4B37, 0x2189, 0x29B1, 0x906E, 0x31C3, 0xEA82, 0xCBF43926, 0x0376E6E7];
        Assert.Equal(expected.Length, CrcCalculator.Models.Count);
        for (int index = 0; index < expected.Length; index++)
        {
            CrcModel model = CrcCalculator.Models[index];
            Assert.Equal(expected[index], CrcCalculator.Compute("123456789"u8, model));
            uint register = CrcCalculator.Append(model.Init, "1234"u8, model);
            Assert.Equal(expected[index], CrcCalculator.Finish(CrcCalculator.Append(register, "56789"u8, model), model));
        }
        Assert.Equal(new byte[] { 0x31, 0x32, 0x33, 0x34 }, CrcCalculator.ParseInput("0x31, 32\n33:34", false));
        Assert.Throws<FormatException>(() => CrcCalculator.ParseInput("123", false));
        Assert.Throws<FormatException>(() => CrcCalculator.ParseInput("GG", false));
        Assert.Throws<FormatException>(() => CrcCalculator.ParseInput("中文", true));
        Assert.Equal("10000000000000000000000000000000", CrcCalculator.Binary(0x80000000, 32));
        Assert.Equal(1u, CrcCalculator.Compute([0x01], new("parity", 1, 1, 0, 0, false, false)));
        Assert.Equal(0u, CrcCalculator.Compute([], CrcCalculator.Models[13]) ^ 0xFFFFu);
    }

    [HostBuildFact]
    public async Task EmbeddedWorkerCalculatesTextFilesAndRejectsInvalidParameters()
    {
        CrcEnvironment environment = new();
        await using BedHarness harness = BedHarness.Create("crc", environment: environment);
        FactoryPackDefinition pack = Assert.Single(PluginSystemHost.BuildFactoryPacks(), item => item.PluginId == "com.ducom.crc");
        string target = Path.Combine(harness.Paths.InstalledRoot, pack.PluginId, pack.Version);
        Directory.CreateDirectory(target);
        foreach (FactoryPackFile file in pack.Files) File.WriteAllBytes(Path.Combine(target, file.RelativePath), file.Content());
        harness.Service.Registry.Mutate(data => data.Plugins[pack.PluginId] = new PluginRegistryEntry
        {
            Id = pack.PluginId, SelectedVersion = pack.Version, Enabled = true,
            ApprovedPermissions = [Permission.FilesUserSelectedRead],
            InstalledVersions = [new InstalledVersionRecord { Version = pack.Version, Path = target, Digest = PluginPackageInstaller.ComputeDirectoryDigest(target) }],
        });
        PluginRuntimeController controller = Assert.IsType<PluginRuntimeController>(harness.Service.GetOrCreateController(pack.PluginId));
        Assert.True(await harness.Service.StartRegisteredAsync(pack.PluginId));
        Dictionary<string, string> values = new() { ["data"] = "123456789", ["mode"] = "ascii", ["width"] = "16", ["poly"] = "8005", ["init"] = "FFFF", ["xor"] = "0", ["refin"] = "true", ["refout"] = "true" };
        Assert.True(await controller.InvokeCommandAsync("calculate", values));
        using (JsonDocument state = State()) Assert.Equal("4B37", state.RootElement.GetProperty("Hex").GetString());
        environment.Source = Path.Combine(harness.Paths.Root, "crc-input.bin");
        await File.WriteAllBytesAsync(environment.Source, "123456789"u8.ToArray());
        Assert.True(await controller.InvokeCommandAsync("file", values));
        using (JsonDocument state = State()) Assert.Equal("4B37", state.RootElement.GetProperty("Hex").GetString());
        await File.WriteAllBytesAsync(environment.Source, []);
        Assert.True(await controller.InvokeCommandAsync("file", values));
        using (JsonDocument state = State()) Assert.Equal("FFFF", state.RootElement.GetProperty("Hex").GetString());
        values["poly"] = "10000";
        Assert.True(await controller.InvokeCommandAsync("calculate", values));
        using (JsonDocument state = State()) { Assert.True(state.RootElement.GetProperty("Error").GetBoolean()); Assert.Equal("", state.RootElement.GetProperty("Hex").GetString()); }
        JsonDocument State() => JsonDocument.Parse(environment.Pages.Last().OfType<UiTextNode>().Single().Text);
    }

    [Fact]
    public void NativeWindowSubmitsParametersConvertsInputAndInvalidatesOldResults()
    {
        Exception? failure = null;
        WpfTestDispatcher.Run(() =>
        {
            try
            {
                using PluginUiTestScope scope = new();
                IReadOnlyDictionary<string, string>? submitted = null;
                CrcWindow window = new((command, values) => { submitted = values; return Task.FromResult(true); });
                scope.Track(window);
                // The production host supplies these dynamic tokens; isolate them to this test window.
                window.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri("/DuCom;component/Resources/DesignTokens.Colors.Dark.xaml", UriKind.Relative),
                });
                try
                {
                    window.Show();
                    Capture(window, "crc-initial.png");
                    ComboBox models = (ComboBox)window.FindName("ModelSelect");
                    Assert.Contains(VisualTexts(models), text => text.Text == "CRC-16/MODBUS");
                    Assert.DoesNotContain(VisualTexts(models), text => text.Text.Contains("CrcModel {", StringComparison.Ordinal));
                    ToggleDropDown(models);
                    Assert.True(models.IsDropDownOpen, "The template toggle must open the model dropdown.");
                    ComboBoxItem longestModel = (ComboBoxItem)models.ItemContainerGenerator.ContainerFromIndex(16);
                    Assert.Contains(VisualTexts(longestModel), text => text.Text == "CRC-16/CCITT-FALSE");
                    Capture(window, "crc-model-dropdown.png");
                    // ComboBoxItem delegates automation to its owning selector; it does not
                    // guarantee a stand-alone SelectionItem provider for the realized container.
                    longestModel.SetCurrentValue(ComboBoxItem.IsSelectedProperty, true);
                    Assert.Equal("CRC-16/CCITT-FALSE", ((CrcModel)models.SelectedItem).Name);
                    ((IExpandCollapseProvider)new ComboBoxAutomationPeer(models).GetPattern(PatternInterface.ExpandCollapse)!).Collapse();
                    Assert.False(models.IsDropDownOpen);
                    ToggleDropDown(models);
                    Assert.True(models.IsDropDownOpen, "The template toggle must reopen the dropdown.");
                    models.IsDropDownOpen = false;
                    models.SelectedItem = CrcCalculator.Models.Single(model => model.Name == "CRC-16/MODBUS");
                    TextBox input = (TextBox)window.FindName("DataInput");
                    input.Text = "31 32 33 34 35 36 37 38 39";
                    ((RadioButton)window.FindName("AsciiMode")).IsChecked = true;
                    Assert.Equal("123456789", input.Text);
                    ((Button)window.FindName("CalculateButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.NotNull(submitted);
                    Assert.Equal("ascii", submitted["mode"]);
                    Assert.Equal("8005", submitted["poly"]);
                    typeof(CrcWindow).GetMethod("UpdateState", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(window,
                        [new UiNode[] { new UiTextNode { FieldId = "native-state", Text = JsonSerializer.Serialize(new { Hex = "4B37", Bin = "0100101100110111", Status = "计算完成 · 9 字节", Error = false, Busy = false }) } }]);
                    Assert.Equal("4B37", ((TextBox)window.FindName("HexResult")).Text);
                    Capture(window, "crc-1040.png");
                    window.Width = 840; window.Height = 720;
                    Capture(window, "crc-840.png");
                    input.Text = "changed";
                    Assert.False(((Button)window.FindName("CopyHexButton")).IsEnabled);
                    Assert.Equal("—", ((TextBox)window.FindName("HexResult")).Text);
                    ((ComboBox)window.FindName("ModelSelect")).SelectedIndex = 0;
                    Assert.True(((TextBox)window.FindName("PolyInput")).IsEnabled);
                    ComboBox widths = (ComboBox)window.FindName("WidthInput");
                    ToggleDropDown(widths);
                    Assert.True(widths.IsDropDownOpen, "The custom WIDTH template toggle must open the dropdown.");
                    widths.IsDropDownOpen = false;
                    Assert.Equal(21, ((DataGrid)window.FindName("ModelTable")).Items.Count);
                }
                finally { window.Close(); }
            }
            catch (Exception exception) { failure = exception; }
        });
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void Capture(Window window, string name)
    {
        FrameworkElement content = (FrameworkElement)window.Content;
        content.Measure(new Size(window.Width, window.Height)); content.Arrange(new Rect(0, 0, window.Width, window.Height)); content.UpdateLayout();
        string? directory = Environment.GetEnvironmentVariable("DUCOM_CRC_SCREENSHOTS");
        if (directory is null) return;
        RenderTargetBitmap bitmap = new((int)window.Width, (int)window.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        Directory.CreateDirectory(directory);
        PngBitmapEncoder encoder = new(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(Path.Combine(directory, name)); encoder.Save(stream);
    }

    private static IEnumerable<TextBlock> VisualTexts(DependencyObject root)
    {
        if (root is TextBlock text) yield return text;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (TextBlock child in VisualTexts(VisualTreeHelper.GetChild(root, index))) yield return child;
    }

    private static void ToggleDropDown(ComboBox combo)
    {
        // Exercise the shipped template's TwoWay toggle binding without moving the system
        // cursor. Native mouse hit-testing/capture remains an interactive-desktop system
        // test boundary; UIAutomation here is deterministic even on a locked build agent.
        combo.ApplyTemplate();
        ToggleButton toggle = Assert.IsType<ToggleButton>(combo.Template.FindName("DropDownToggle", combo));
        Assert.True(toggle.IsHitTestVisible);
        Assert.Equal(HorizontalAlignment.Stretch, toggle.HorizontalAlignment);
        Assert.Equal(VerticalAlignment.Stretch, toggle.VerticalAlignment);
        ((IToggleProvider)new ToggleButtonAutomationPeer(toggle).GetPattern(PatternInterface.Toggle)!).Toggle();
        DispatcherFrame frame = new();
        combo.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
        combo.UpdateLayout();
    }

    private sealed class CrcEnvironment : FakeEnvironment
    {
        public string Source { get; set; } = "";
        public ConcurrentQueue<IReadOnlyList<UiNode>> Pages { get; } = new();
        public override Task<HostPickResult?> PickReadAsync(string pluginId, HostPickRequest request, CancellationToken cancellationToken) =>
            Task.FromResult<HostPickResult?>(new HostPickResult { DisplayPath = Source, IsDirectory = false, Remembered = false });
        public override void UpdateToolPage(string pluginId, string contributionId, IReadOnlyList<UiNode> nodes) => Pages.Enqueue(nodes);
    }
}
