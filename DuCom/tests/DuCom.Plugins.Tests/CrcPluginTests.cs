using System.Collections.Concurrent;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Runtime.InteropServices;
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
        Thread thread = new(() =>
        {
            try
            {
                Application app = Application.Current ?? new Application();
                app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary { Theme = Wpf.Ui.Appearance.ApplicationTheme.Dark });
                app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
                IReadOnlyDictionary<string, string>? submitted = null;
                CrcWindow window = new((command, values) => { submitted = values; return Task.FromResult(true); });
                // The production host supplies these dynamic tokens; isolate them to this test window.
                window.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri("/DuCom;component/Resources/DesignTokens.Colors.Dark.xaml", UriKind.Relative),
                });
                try
                {
                    window.Show();
                    window.UpdateLayout();
                    ComboBox models = (ComboBox)window.FindName("ModelSelect");
                    Assert.Contains(VisualTexts(models), text => text.Text == "CRC-16/MODBUS");
                    Assert.DoesNotContain(VisualTexts(models), text => text.Text.Contains("CrcModel {", StringComparison.Ordinal));
                    ClickAt(models, new Point(models.ActualWidth / 2, models.ActualHeight / 2));
                    Assert.True(models.IsDropDownOpen, "Clicking the model field must open the dropdown.");
                    ComboBoxItem longestModel = (ComboBoxItem)models.ItemContainerGenerator.ContainerFromIndex(16);
                    Assert.Contains(VisualTexts(longestModel), text => text.Text == "CRC-16/CCITT-FALSE");
                    Capture(window, "crc-model-dropdown.png");
                    ClickAt(longestModel, new Point(40, longestModel.ActualHeight / 2));
                    Assert.False(models.IsDropDownOpen);
                    Assert.Equal("CRC-16/CCITT-FALSE", ((CrcModel)models.SelectedItem).Name);
                    ClickAt(models, new Point(models.ActualWidth - 16, models.ActualHeight / 2));
                    Assert.True(models.IsDropDownOpen, "Clicking the arrow must reopen the dropdown.");
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
                    ClickAt(widths, new Point(widths.ActualWidth / 2, widths.ActualHeight / 2));
                    Assert.True(widths.IsDropDownOpen, "The custom WIDTH dropdown must open on click.");
                    widths.IsDropDownOpen = false;
                    Assert.Equal(21, ((DataGrid)window.FindName("ModelTable")).Items.Count);
                }
                finally { window.Close(); }
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
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

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);

    private static void ClickAt(FrameworkElement element, Point point)
    {
        element.BringIntoView();
        element.UpdateLayout();
        // BringIntoView queues popup scrolling. Let the dispatcher settle before converting
        // coordinates, otherwise short/high-DPI popup viewports click the old offscreen row.
        DispatcherFrame layoutFrame = new();
        element.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => layoutFrame.Continue = false);
        Dispatcher.PushFrame(layoutFrame);
        Point screen = element.PointToScreen(point);
        Assert.True(SetCursorPos((int)screen.X, (int)screen.Y));
        // Deliver WM_MOUSEMOVE before the button events: popup capture routes using
        // Mouse.DirectlyOver, which otherwise still points at the combo's old position.
        DispatcherFrame pointerFrame = new();
        element.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => pointerFrame.Continue = false);
        Dispatcher.PushFrame(pointerFrame);
        mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
        mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
        DispatcherFrame frame = new();
        DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
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
