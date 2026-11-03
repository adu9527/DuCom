using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DuCom.Plugin;
using DuCom.Plugins.Production;
using DuCom.Services.Plugins;
using Xunit;

namespace DuCom.Plugins.Tests;

public sealed class ProductionTests
{
    [Theory]
    [InlineData("21 04", "55 AA 02 21 04 DE")]
    [InlineData("94 01", "55 AA 02 94 01 1C")]
    [InlineData("97 01", "55 AA 02 97 01 49")]
    [InlineData("98 01", "55 AA 02 98 01 51")]
    [InlineData("21 F0 34 01 00", "55 AA 05 21 F0 34 01 00 93")]
    public void FramesMatchActualProjectDocuments(string payload, string expected)
    {
        byte[] frame = ProtocolCodec.BuildFrame(new(), new() { Hex = payload });
        Assert.Equal(expected, Hex.Format(frame));
        Assert.True(ProtocolCodec.CheckChecksum(new(), frame));
        frame[^1] ^= 1;
        Assert.False(ProtocolCodec.CheckChecksum(new(), frame));
    }

    [Fact]
    public void DecoderHandlesNoiseSplitHeadersConcatenationAndIndependentChannels()
    {
        FrameDecoder left = new(new());
        FrameDecoder right = new(new());
        Assert.Empty(left.Feed(Hex.Parse("12 34 55")));
        Assert.Empty(right.Feed(Hex.Parse("55 AA 03 97")));
        var frames = left.Feed(Hex.Parse("AA 02 21 04 DE 55 AA 02 94 01 1C"));
        Assert.Equal(2, frames.Count);
        Assert.Single(right.Feed(Hex.Parse("02 63 00")));
        Assert.Empty(left.Feed([]));
    }

    [Fact]
    public void FieldsDecodeActualLayoutsAndRejectShortResponses()
    {
        ProductionCommand voltage = new() { Fields = [new() { Type = FieldType.UInt16, Offset = 5, Unit = "mV" }] };
        Assert.Equal("3957 mV", ProtocolCodec.Decode(voltage, Hex.Parse("55 AA 04 98 02 0F 75 00")));
        voltage.Fields[0].LittleEndian = true;
        voltage.Fields[0].Offset = 6;
        Assert.Equal("3957 mV", ProtocolCodec.Decode(voltage, Hex.Parse("55 AB CC 0B 03 11 75 0F 00")));
        ProductionCommand mac = new() { Fields = [new() { Type = FieldType.Mac, Offset = 7 }] };
        Assert.Equal("65:61:96:2F:2B:2A", ProtocolCodec.Decode(mac, Hex.Parse("55 AA 0A 21 F0 41 02 65 61 96 2F 2B 2A 00")));
        Assert.Throws<InvalidDataException>(() => ProtocolCodec.Decode(mac, [0x55]));
        mac.StatusOffset = 1; mac.StatusValue = 0;
        Assert.Throws<InvalidDataException>(() => ProtocolCodec.Decode(mac, [0x55, 0xAA]));
    }

    [Fact]
    public void ProfileRoundTripRetainsSelectionChannelTimingAndValidation()
    {
        ProductionProfile profile = ProductionProfile.CreateExample();
        profile.Commands[1].Channel = EarChannel.Right;
        profile.Commands[1].DelayMs = 1000;
        ProductionProfile copy = ProductionProfile.FromJson(profile.ToJson());
        Assert.Equal(EarChannel.Right, copy.Commands[1].Channel);
        Assert.Equal(1000, copy.Commands[1].DelayMs);
        Assert.False(copy.Commands.Single(c => c.Id == "pair-left").Enabled);
        Assert.DoesNotContain(copy.Commands, c => c.Hex == "21 04");
        copy.Commands[0].DelayMs = 5001;
        Assert.Throws<InvalidDataException>(copy.Validate);
        Assert.Throws<InvalidDataException>(() => ProductionProfile.FromJson("{\"SchemaVersion\":9}"));
        Assert.Throws<JsonException>(() => ProductionProfile.FromJson("{\"Unexpected\":true}"));
    }

    [Fact]
    public void PairingCopiesPeerPayloadAndRebuildsLengthAndCrc()
    {
        ProductionProfile p = ProductionProfile.CreateExample();
        byte[] source = new byte[36];
        source[0] = 0x55; source[1] = 0xAA; source[2] = 0x20; source[3] = 1;
        for (int i = 4; i < 35; i++) source[i] = (byte)i;
        ProtocolCodec.WriteChecksum(p.Protocol, source);
        ProductionCommand write = p.Commands.Single(c => c.Id == "pair-write-left");
        byte[] frame = ProtocolCodec.BuildFrame(p.Protocol, write, source);
        Assert.Equal(36, frame.Length);
        Assert.Equal(0x20, frame[2]); Assert.Equal(2, frame[3]);
        Assert.Equal(source[4..35], frame[4..35]);
        Assert.True(ProtocolCodec.CheckChecksum(p.Protocol, frame));
        Assert.Throws<InvalidDataException>(() => ProtocolCodec.BuildFrame(p.Protocol, write, [0x55, 0xAA]));
    }

    [Fact]
    public void PreviewCalculatesKnownAncAndTwoMacReplyWithoutSerial()
    {
        var anc = CommandPreview.Calculate(new() { Hex = "A6 01", ResponseMatch = "A6 02", TxLength = 2, RxLength = 2 }, new());
        Assert.Equal("55 AA 02 A6 01 A0", anc.Tx); Assert.Equal("55 AA 02 A6 02 42", anc.Rx);
        ProductionCommand c = new() { Hex = "AC 01", ResponseMatch = "AC 02", Fields = [new() { Name = "本机", Type = FieldType.Mac, Example = "65 61 96 2F 2B 2A" }, new() { Name = "对耳", Type = FieldType.Mac, Example = "64 C6 48 F3 4D 05" }] };
        CommandPreview.ArrangeFields(c, new());
        Assert.Equal(5, c.Fields[0].Offset); Assert.Equal(11, c.Fields[1].Offset);
        var preview = CommandPreview.Calculate(c, new());
        Assert.Equal("本机：65:61:96:2F:2B:2A · 对耳：64:C6:48:F3:4D:05", ProtocolCodec.Decode(c, Hex.Parse(preview.Rx)));
        Assert.True(ProtocolCodec.CheckChecksum(new(), Hex.Parse(preview.Rx)));
    }
    [Fact]
    public void PairingTemplateIsOneItemWithFourOrderedInternalTransfers()
    {
        ProductionCommand group = ProductionCommand.CreateX8IPairing();
        new ProductionProfile { Commands = [group] }.Validate();
        Assert.Equal(4, group.Steps.Count);
        Assert.Equal("01 02", group.Steps[0].Hex);
        Assert.Equal("01 01", group.Steps[1].Hex);
        Assert.Equal("read-right", group.Steps[2].ResponseSourceId);
        Assert.Equal("read-left", group.Steps[3].ResponseSourceId);
        ProductionProfile copy = ProductionProfile.FromJson(new ProductionProfile { Commands = [group] }.ToJson());
        Assert.Single(copy.Commands); Assert.Equal(4, copy.Commands[0].Steps.Count);
    }
    [Fact]
    public void ResultsRetainContentAndAppendIndependentStatusMarks()
    {
        ProductionResultRow row = new(new()) { Left = "99%", Right = "应答超时" };
        Assert.Equal("99%", row.Left); Assert.Equal("✓", row.LeftMark); Assert.Equal("✕", row.RightMark);
        row.Left = "—"; row.Right = "组队失败 · 来源数据不足";
        Assert.Equal("", row.LeftMark); Assert.Equal("✕", row.RightMark);
        row.Left = new string('A', 100);
        Assert.Equal(41, row.LeftShort.Length); Assert.Equal(100, row.Left.Length);
    }
    [Fact]
    public void FullX8IProfileContainsUniqueCommandsAndNoPairingReadbackRules()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "profiles", "X8I_V1.json"))) directory = directory.Parent;
        Assert.NotNull(directory);
        ProductionProfile profile = ProductionProfile.FromJson(File.ReadAllText(Path.Combine(directory!.FullName, "profiles", "X8I_V1.json")));
        Assert.Equal("X8I_V1", profile.Name);
        Assert.True(profile.Commands.Count >= 50);
        Assert.Equal(profile.Commands.Count, profile.Commands.Select(c => c.Name).Distinct().Count());
        ProductionCommand pairing = profile.Commands.Single(c => c.Steps.Count > 0);
        Assert.Empty(pairing.Comparisons); Assert.Equal(4, pairing.Steps.Count);
        Assert.Contains(profile.Commands, c => c.Id == "peer-mac");
    }
    [Fact]
    public void BuiltinActivationFitsUiLimitsAndPackIsPresent()
    {
        PluginActivation activation = new DuCom.Plugins.Production.Plugin().ActivateAsync(default).Result;
        Assert.True(UiContributionValidator.Validate(activation.ToolPages.Single().Nodes, out string? error), error);
        Assert.Contains(PluginSystemHost.BuildFactoryPacks(), pack => pack.PluginId == "com.ducom.production");
    }

    [Fact]
    public void WindowAndTwoPageEditorRenderAtBothSizes()
    {
        // WPF Application is process-global; render explicitly in its own test run.
        if (Environment.GetEnvironmentVariable("DUCOM_PRODUCTION_SCREENSHOTS") is null) return;
        Exception? failure = null;
        Thread thread = new(() =>
        {
            try
            {
                ProductionWindow window = new((_, _) => Task.FromResult(true));
                PluginActivation activation = new DuCom.Plugins.Production.Plugin().ActivateAsync(default).Result;
                typeof(ProductionWindow).GetMethod("UpdateState", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [activation.ToolPages.Single().Nodes]);
                window.Show();
                ComboBox portPicker = (ComboBox)window.FindName("LeftPort");
                portPicker.ItemsSource = new[] { "COM42", "COM40" }; portPicker.SelectedIndex = 0;
                portPicker.ApplyTemplate(); portPicker.IsDropDownOpen = true; window.UpdateLayout();
                Assert.True(portPicker.IsDropDownOpen);
                var portPopup = (System.Windows.Controls.Primitives.Popup)portPicker.Template.FindName("PART_Popup", portPicker);
                Assert.Same(portPicker, portPopup.PlacementTarget);
                portPicker.IsDropDownOpen = false;
                Capture(window, "production-1040.png");
                DataGrid commands = (DataGrid)window.FindName("ResultsGrid");
                Assert.Equal(4, commands.Items.Count);
                Assert.Equal(3, commands.Columns.Count);
                Assert.NotNull(window.FindName("MaximizeButton"));
                window.Width = 760; window.Height = 600;
                Capture(window, "production-900.png");
                foreach (string name in new[] { "StopButton", "StartButton", "StatisticsText" })
                {
                    FrameworkElement element = (FrameworkElement)window.FindName(name);
                    Point origin = element.TranslatePoint(new Point(), (UIElement)window.Content);
                    Assert.InRange(origin.Y + element.ActualHeight, 0, 600);
                }
                window.WindowState = WindowState.Maximized;
                window.UpdateLayout();
                Assert.Equal(WindowState.Maximized, window.WindowState);
                FrameworkElement footerButton = (FrameworkElement)window.FindName("StopButton");
                Point screenBottom = footerButton.PointToScreen(new Point(0, footerButton.ActualHeight));
                double dpi = VisualTreeHelper.GetDpi(window).DpiScaleY;
                Assert.True(screenBottom.Y / dpi <= SystemParameters.WorkArea.Bottom + 2, $"Stop button outside work area: {screenBottom.Y / dpi}");
                window.WindowState = WindowState.Normal;
                ProductionProfileEditor editor = new(ProductionProfile.CreateExample()) { Owner = window };
                editor.Show();
                Capture(editor, "production-settings.png");
                Assert.Empty(Descendants<TabControl>(editor));
                Assert.Contains(Descendants<DataGrid>(editor), g => g.Columns.Any(c => Equals(c.Header, "超时 ms")) && g.Columns.Any(c => Equals(c.Header, "间隔 ms")));
                TextBox delayInput = Descendants<TextBox>(editor).First(t => System.Windows.Automation.AutomationProperties.GetName(t) == "间隔 ms");
                delayInput.Text = "350";
                Assert.Equal(350, editor.Profile.Commands[0].DelayMs);
                Assert.DoesNotContain(Descendants<DataGrid>(editor).SelectMany(g => g.Columns), c => Equals(c.Header, "通道"));
                CheckBox enabledBox = Descendants<CheckBox>(editor).First();
                bool before = editor.Profile.Commands[0].Enabled;
                var peer = new System.Windows.Automation.Peers.CheckBoxAutomationPeer(enabledBox);
                ((System.Windows.Automation.Provider.IToggleProvider)peer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Toggle)!).Toggle();
                Assert.Equal(!before, editor.Profile.Commands[0].Enabled);
                ((System.Windows.Automation.Provider.IToggleProvider)peer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Toggle)!).Toggle();
                Assert.Equal(before, editor.Profile.Commands[0].Enabled);
                ProductionCommandEditor detail = new(new() { Name = "打开ANC", Hex = "A6 01", ResponseMatch = "A6 02" }, new()) { Owner = editor };
                detail.Show(); Capture(detail, "production-command-detail.png");
                Descendants<Button>(detail).Single(b => Equals(b.Content, "计算报文（不发送）")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Contains(Descendants<TextBox>(detail), b => b.Text.Contains("55 AA 02 A6 01 A0", StringComparison.Ordinal));
                detail.Close();
                ProductionCompoundEditor pairingEditor = new(ProductionCommand.CreateX8IPairing(), new()) { Owner = editor };
                pairingEditor.Show(); Capture(pairingEditor, "production-pairing-template.png");
                Assert.Equal(4, Descendants<DataGrid>(pairingEditor).Single().Items.Count);
                pairingEditor.Close();
                editor.Close();
                window.CloseFromHost();
            }
            catch (Exception e) { failure = e; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T item) yield return item;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (T child in Descendants<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static void Capture(Window window, string name)
    {
        FrameworkElement content = (FrameworkElement)window.Content;
        content.Measure(new Size(window.Width, window.Height));
        content.Arrange(new Rect(0, 0, window.Width, window.Height));
        content.UpdateLayout();
        string? directory = Environment.GetEnvironmentVariable("DUCOM_PRODUCTION_SCREENSHOTS");
        if (directory is null) return;
        Directory.CreateDirectory(directory);
        RenderTargetBitmap bitmap = new((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        PngBitmapEncoder encoder = new(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(Path.Combine(directory, name)); encoder.Save(stream);
    }
}
