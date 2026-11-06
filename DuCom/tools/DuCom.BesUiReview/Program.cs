using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DuCom.Plugin;
using DuCom.Plugin.Dto;
using DuCom.Services;
using DuCom.Services.Plugins;

namespace DuCom.BesUiReview;

internal static class Program
{
    private const string PluginId = "internal.ducom.bes-download";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    // One Application, one STA, one short-lived process. Never call App.Run/ActivateAsync.
    [STAThread]
    public static int Main(string[] args)
    {
        DuCom.App? app = null;
        string? output = null;
        try
        {
            string root = FindDuComRoot();
            output = Path.GetFullPath(Option(args, "--output") ?? Path.Combine(root, "artifacts", "bes-compact-review"));
            Directory.CreateDirectory(output);
            string dll = Path.GetFullPath(Option(args, "--bes-dll") ?? Path.Combine(root, "plugins", "BesDownload", "src", "BesDownload.Plugin", "bin", "Release", "net10.0", "BesDownload.Plugin.dll"));
            if (!File.Exists(dll)) throw new FileNotFoundException("Build the local BES plugin in Release first (the review tool never builds or activates it).", dll);
            string dllDirectory = Path.GetDirectoryName(dll)!;
            AssemblyLoadContext.Default.Resolving += (_, name) =>
            {
                Assembly? shared = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == name.Name);
                if (shared is not null) return shared;
                string dependency = Path.Combine(dllDirectory, name.Name + ".dll");
                return File.Exists(dependency) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(dependency) : null;
            };
            Assembly bes = AssemblyLoadContext.Default.LoadFromAssemblyPath(dll);
            Type pluginType = bes.GetType("BesDownload.Plugin.BesDownloadPlugin", throwOnError: true)!;
            app = new ReviewApp();
            // Load the same dictionaries without calling generated App.InitializeComponent:
            // BAML component loading rejects a derived review application type.
            app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary { Theme = Wpf.Ui.Appearance.ApplicationTheme.Dark });
            app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
            foreach (string dictionary in new[] { "CommonControls.xaml", "DesignTokens.xaml", "DesignTokens.Colors.Dark.xaml" })
                app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/DuCom;component/Resources/" + dictionary, UriKind.Relative) });
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("zh-CN");
            CultureInfo.CurrentUICulture = CultureInfo.CurrentCulture;
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/DuCom;component/Resources/Languages/zh-CN.xaml", UriKind.Absolute),
            });
            DensityService.Apply(app.Resources);

            List<object> results = [];
            List<string> failures = [];
            foreach (bool error in new[] { false, true })
            foreach (int ports in new[] { 2, 8 })
            foreach (bool expanded in new[] { false, true })
            {
                string scenario = $"{(error ? "error" : "normal")}-{ports}ports-{(expanded ? "expanded" : "collapsed")}";
                if (Option(args, "--scenario") is { } filter && !scenario.Equals(filter, StringComparison.OrdinalIgnoreCase)) continue;
                foreach (int width in new[] { 1100, 900 })
                foreach (int scale in new[] { 100, 125, 150, 200 })
                {
                    bool filtered = args.Any(argument => argument is "--scenario" or "--width" or "--scale");
                    bool representative = (!error && ports == 2 && !expanded && width == 1100 && scale is 100 or 150 or 200)
                        || (error && ports == 8 && expanded && width == 900 && scale == 125);
                    if (!args.Contains("--all", StringComparer.Ordinal) && !filtered && !representative) continue;
                    if (Option(args, "--width") is { } w && width != int.Parse(w, CultureInfo.InvariantCulture)) continue;
                    if (Option(args, "--scale") is { } s && scale != int.Parse(s, CultureInfo.InvariantCulture)) continue;
                    string name = $"{scenario}-{width}dip-{scale}pct";
                    Console.WriteLine(name);
                    try { results.Add(Review(pluginType, output, name, width, scale, ports, error, expanded)); }
                    catch (Exception exception) { failures.Add(name + ": " + exception); Console.Error.WriteLine(exception); }
                }
            }
            if (results.Count == 0 && failures.Count == 0) throw new ArgumentException("No cases matched the filters.");
            File.WriteAllText(Path.Combine(output, "review.json"), JsonSerializer.Serialize(new
            {
                tool = "DuCom.BesUiReview", pluginId = PluginId, dll,
                dllSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(dll))),
                assembly = bes.FullName,
                productAssembly = typeof(PluginToolWindow).Assembly.Location,
                productSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(PluginToolWindow).Assembly.Location))),
                safety = "No Application.Run, BES activation, device API, worker, persistence or real command execution. Only fixture fields and WPF events are exercised.",
                dpiNote = "125/150/200 are RenderTargetBitmap output-DPI variants at a fixed DIP layout, not a physical per-monitor/DPI-transition test. Actual layout DPI is recorded separately.",
                expectedCompactBaseline = "Parent target: 13 DIP body, 30 DIP buttons, 16 DIP checkbox marker. Raw measurements are authoritative; no legacy 14-DIP assertion.",
                results, failures,
            }, JsonOptions));
            Console.WriteLine($"Review finished: {results.Count} passed, {failures.Count} failed. Output: {output}");
            return failures.Count == 0 ? 0 : 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            if (output is not null) File.WriteAllText(Path.Combine(output, "fatal.txt"), exception.ToString());
            return 1;
        }
        finally
        {
            // No application startup was dispatched. Closing every window terminates this isolated fixture.
            if (app is not null)
            {
                foreach (Window window in app.Windows.Cast<Window>().ToArray()) window.Close();
                app.Shutdown();
                app.Dispose();
            }
        }
    }

    private static object Review(Type pluginType, string output, string name, int width, int scale, int portCount, bool error, bool expanded)
    {
        object plugin = Activator.CreateInstance(pluginType)!;
        SerialPortInfo[] ports = Enumerable.Range(3, portCount).Select(i => new SerialPortInfo { Port = $"COM{i}", DisplayName = $"Fixture USB Serial COM{i}", DeviceIdentity = $"fixture-{i}" }).ToArray();
        PrivateField(pluginType, "_ports").SetValue(plugin, ports);
        Dictionary<string, string> form = (Dictionary<string, string>)PrivateField(pluginType, "_form").GetValue(plugin)!;
        form["factory-sector"] = "true";
        form["app-enabled"] = "true";
        form["ota-enabled"] = "true";
        form["bt-name"] = "BES_Review_BT";
        form["ble-name"] = "BES_Review_BLE";
        foreach (SerialPortInfo port in ports) form["port:" + port.Port] = "true";
        MethodInfo build = pluginType.GetMethod("BuildPage", BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new MissingMethodException(pluginType.FullName, "BuildPage");
        IReadOnlyList<UiNode> original = (IReadOnlyList<UiNode>)build.Invoke(plugin, null)!;
        UiNode[] nodes = original.Select(node => Fixture(node, error, expanded)).ToArray();
        if (!UiContributionValidator.Validate(nodes, out string? validation)) throw new InvalidOperationException(validation);
        List<CommandRecord> commands = [];
        ToolPageContribution page = new()
        {
            ContributionId = "bes", Title = "BES 烧录", PreferredWidth = width, MinWidth = 900,
            Watermark = "禾胜成内部专用", WatermarkFontSizeDelta = 6, Nodes = nodes,
        };
        PluginToolWindow window = new(PluginId, page, "BES 烧录", (commandId, values) =>
        {
            commands.Add(new CommandRecord(commandId, new Dictionary<string, string>(values, StringComparer.Ordinal)));
            return Task.FromResult(true);
        });
        try
        {
            window.Topmost = false; // In-memory only; never click the preference-saving footer toggle.
            window.ShowActivated = false;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = 0;
            window.Top = 0;
            window.Width = width;
            window.SetContent(nodes);
            window.Show();
            Drain();
            FrameworkElement shell = (FrameworkElement)window.Content;
            ScrollViewer viewer = ((Grid)shell).Children.OfType<ScrollViewer>().Single();
            FrameworkElement content = viewer.Content as FrameworkElement ?? throw new InvalidOperationException("Host failed to render a page root.");
            Layout(shell, shell.ActualWidth, shell.ActualHeight);
            Drain();
            Dictionary<string, string> expected = ExpectedValues(nodes);
            object contract = CheckContract(content, nodes, expected, commands);
            Drain();
            Layout(shell, shell.ActualWidth, shell.ActualHeight);
            DpiScale actualDpi = VisualTreeHelper.GetDpi(shell);
            List<object> measurements = MeasureControls(content, scale / 100d);
            object viewport = Capture(shell, Path.Combine(output, name + ".png"), scale / 100d);
            object viewportSize = new { width = shell.ActualWidth, height = shell.ActualHeight, pageWidth = content.ActualWidth, pageHeight = content.ActualHeight, viewportWidth = viewer.ViewportWidth, viewportHeight = viewer.ViewportHeight, horizontalOverflow = viewer.ScrollableWidth, verticalOverflow = viewer.ScrollableHeight };

            // Also capture the real page root at its complete extent so multi-port/settings
            // cases are inspectable below the viewport. No replacement UI is constructed.
            double pageWidth = content.ActualWidth;
            content.Measure(new Size(pageWidth, double.PositiveInfinity));
            double pageHeight = Math.Max(1, content.DesiredSize.Height);
            Layout(content, pageWidth, pageHeight);
            object fullPage = Capture(content, Path.Combine(output, name + "-page.png"), scale / 100d);
            return new
            {
                name, widthDip = width, outputScalePercent = scale, portCount, errorPath = error, settingsExpanded = expanded,
                windowHeightDip = window.ActualHeight, windowMinHeightDip = window.MinHeight,
                actualLayoutDpi = new { actualDpi.DpiScaleX, actualDpi.DpiScaleY }, viewportSize, viewport, fullPage,
                contract, measurements, commands,
            };
        }
        finally { window.CloseFromHost(); Drain(); }
    }

    private static UiNode Fixture(UiNode node, bool error, bool expanded)
    {
        if (node is UiExpanderNode expander) return expander with { IsExpanded = expanded, Children = expander.Children.Select(child => Fixture(child, error, expanded)).ToArray() };
        if (node is UiLabelNode { Id: "operation-status" } status)
            return status with { Text = error ? "固件文件已失效，请重新选择后再开始烧录" : "就绪", Style = error ? UiTextStyle.Warning : UiTextStyle.Accent };
        if (node is not UiPanelNode panel) return node;
        UiNode[] children = panel.Children.Select(child => Fixture(child, error, expanded)).ToArray();
        UiButtonNode? picker = children.OfType<UiButtonNode>().FirstOrDefault(button => button.CommandId is "pick-app" or "pick-ota");
        if (picker is not null)
        {
            bool ota = picker.CommandId == "pick-ota";
            string path = error
                ? @"C:\Firmware\旧版本归档\BES2700_测试固件_路径不存在\build_2026_11_05\release\" + (ota ? "ota_application.bin" : "best2700_application.bin") + "（需重新选择）"
                : @"C:\Firmware\BES2700\release\" + (ota ? "ota_application.bin" : "best2700_application.bin");
            children = children.Select(child => child switch
            {
                UiLabelNode label => label with { Text = path, Style = error ? UiTextStyle.Warning : UiTextStyle.Caption },
                UiButtonNode button when button.CommandId == picker.CommandId => button with { Text = ota ? "更换 OTA" : "更换固件" },
                _ => child,
            }).ToArray();
        }
        return panel with { Children = children };
    }

    private static object CheckContract(FrameworkElement root, IReadOnlyList<UiNode> nodes, Dictionary<string, string> expected, List<CommandRecord> commands)
    {
        // Expand only for contract collection, then restore the requested screenshot state.
        Expander[] expanders = Walk(root).OfType<Expander>().ToArray();
        bool[] states = expanders.Select(e => e.IsExpanded).ToArray();
        foreach (Expander e in expanders) e.IsExpanded = true;
        Drain();
        root.UpdateLayout();
        IReadOnlyDictionary<string, string> initial = PluginUiRenderer.CollectFormValues(root);
        string[] missing = expected.Keys.Except(initial.Keys, StringComparer.Ordinal).ToArray();
        string[] mismatches = expected.Where(pair => !initial.TryGetValue(pair.Key, out string? actual) || pair.Value != actual).Select(pair => pair.Key).ToArray();
        if (missing.Length > 0 || mismatches.Length > 0) throw new InvalidOperationException("Form contract lost: missing=" + string.Join(',', missing) + "; mismatch=" + string.Join(',', mismatches));
        string[] textboxTags = Walk(root).OfType<TextBox>().Select(box => box.Tag as string ?? "<missing>").ToArray();
        string[] expectedTextTags = Flatten(nodes).OfType<UiTextNode>().Select(text => text.FieldId).ToArray();
        if (!textboxTags.Order(StringComparer.Ordinal).SequenceEqual(expectedTextTags.Order(StringComparer.Ordinal))) throw new InvalidOperationException("TextBox Tag contract was lost.");
        var checkboxStates = Flatten(nodes).OfType<UiCheckBoxNode>().Select(node =>
        {
            CheckBox visual = Walk(root).OfType<CheckBox>().Single(check => check.Tag as string == node.FieldId);
            if (visual.IsChecked != node.IsChecked || visual.IsEnabled != node.IsEnabled) throw new InvalidOperationException("Checkbox state changed: " + node.FieldId);
            return new { node.FieldId, visual.IsEnabled, visual.IsChecked };
        }).ToArray();
        // All real button click handlers must still point at the record-only router.
        List<string> verifiedButtonCommands = [];
        Dictionary<string, int> occurrences = new(StringComparer.Ordinal);
        foreach (UiButtonNode node in Flatten(nodes).OfType<UiButtonNode>())
        {
            int occurrence = occurrences.GetValueOrDefault(node.Text);
            occurrences[node.Text] = occurrence + 1;
            // BT/BLE both label their independent commands '随机生成'. Match stable tree order.
            Button visual = Walk(root).OfType<Button>().Where(button => button.Content as string == node.Text).ElementAt(occurrence);
            bool oldEnabled = visual.IsEnabled;
            int before = commands.Count;
            visual.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, visual));
            Drain();
            if (!commands.Skip(before).Any(command => command.CommandId == node.CommandId)) throw new InvalidOperationException("Button command was lost: " + node.CommandId);
            visual.IsEnabled = oldEnabled; // Restore DisableOnClick; no product command is executed.
            verifiedButtonCommands.Add(node.CommandId);
        }
        commands.Clear();
        Button refresh = Walk(root).OfType<Button>().Single(button => button.Content as string == "刷新串口");
        refresh.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, refresh));
        Drain();
        CommandRecord routed = commands.LastOrDefault(c => c.CommandId == "refresh-ports") ?? throw new InvalidOperationException("Refresh click did not reach the real host command router.");
        if (expected.Any(pair => !routed.Values.TryGetValue(pair.Key, out string? value) || pair.Value != value)) throw new InvalidOperationException("Refresh SubmitForm did not preserve the full field/value contract.");
        CheckBox checkbox = Walk(root).OfType<CheckBox>().Single(check => check.Tag as string == "erase-chip");
        bool? oldChecked = checkbox.IsChecked;
        checkbox.IsChecked = !oldChecked.GetValueOrDefault();
        Drain();
        string changedValue = checkbox.IsChecked == true ? "true" : "false";
        UiCheckBoxNode checkNode = Flatten(nodes).OfType<UiCheckBoxNode>().Single(check => check.FieldId == "erase-chip");
        bool changeRouted = commands.Any(c => c.CommandId == checkNode.CommandId && c.Values.TryGetValue("erase-chip", out string? value) && value == changedValue);
        if (!changeRouted) throw new InvalidOperationException("Checkbox field/change-command event was lost.");
        checkbox.IsChecked = oldChecked;
        TextBox input = Walk(root).OfType<TextBox>().Single(box => box.Tag as string == "bt-name");
        string oldText = input.Text;
        input.Text = "BES_fixture_字段契约";
        refresh.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, refresh));
        Drain();
        if (!commands.Any(c => c.CommandId == "refresh-ports" && c.Values.TryGetValue("bt-name", out string? value) && value == input.Text)) throw new InvalidOperationException("Edited textbox field was not submitted.");
        input.Text = oldText;
        for (int i = 0; i < expanders.Length; i++) expanders[i].IsExpanded = states[i];
        Drain();
        return new { expectedFields = expected, actualFields = initial, textboxTags, checkboxStates, verifiedButtonCommands, missing, mismatches, refreshClickRouted = true, checkboxChangeRouted = changeRouted, editedTextSubmitted = true };
    }

    private static Dictionary<string, string> ExpectedValues(IEnumerable<UiNode> nodes)
    {
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        foreach (UiNode node in Flatten(nodes))
        {
            switch (node)
            {
                case UiTextNode text: values.Add(text.FieldId, text.Text); break;
                case UiCheckBoxNode check: values.Add(check.FieldId, check.IsChecked ? "true" : "false"); break;
                case UiSelectNode select when select.Selected is not null: values.Add(select.FieldId, select.Selected); break;
                case UiSliderNode slider: values.Add(slider.FieldId, slider.Value.ToString("0.###", CultureInfo.InvariantCulture)); break;
            }
        }
        return values;
    }

    private static List<object> MeasureControls(FrameworkElement root, double scale)
    {
        List<object> result = [];
        foreach (FrameworkElement element in Walk(root).OfType<FrameworkElement>())
        {
            if (element is not TextBlock && element is not Button && element is not TextBox && element is not ComboBox && element is not CheckBox) continue;
            double font = element is Control control ? control.FontSize : ((TextBlock)element).FontSize;
            string text = element switch { TextBlock block => block.Text, TextBox box => box.Text, ContentControl cc => cc.Content?.ToString() ?? "", _ => "" };
            Point origin = element.TransformToAncestor(root).Transform(new Point());
            object? marker = null;
            if (element is CheckBox check)
            {
                check.ApplyTemplate();
                FrameworkElement? box = check.Template?.FindName("Marker", check) as FrameworkElement
                    ?? Walk(check).OfType<Border>().FirstOrDefault(border => border.Name.Contains("Box", StringComparison.OrdinalIgnoreCase) && border.ActualWidth > 0);
                if (box is not null) marker = new { box.Name, widthDip = box.ActualWidth, heightDip = box.ActualHeight, widthPixels = box.ActualWidth * scale, heightPixels = box.ActualHeight * scale };
            }
            result.Add(new
            {
                type = element.GetType().FullName, element.Name, fieldId = element.Tag as string, text,
                fontSizeDip = font, fontSizePixels = font * scale,
                widthDip = element.ActualWidth, heightDip = element.ActualHeight,
                widthPixels = element.ActualWidth * scale, heightPixels = element.ActualHeight * scale,
                xDip = origin.X, yDip = origin.Y, element.IsVisible, element.IsEnabled,
                checkboxMarker = marker,
            });
        }
        return result;
    }

    private static object Capture(FrameworkElement root, string path, double scale)
    {
        int width = Math.Max(1, (int)Math.Ceiling(root.ActualWidth * scale));
        int height = Math.Max(1, (int)Math.Ceiling(root.ActualHeight * scale));
        if (width > 12000 || height > 20000) throw new InvalidOperationException($"Unexpected capture size {width}x{height}.");
        RenderTargetBitmap bitmap = new(width, height, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(root);
        int stride = width * 4;
        byte[] pixels = new byte[stride * height];
        bitmap.CopyPixels(pixels, stride, 0);
        long nonTransparent = 0;
        HashSet<int> colors = [];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i + 3] > 0) nonTransparent++;
            if (colors.Count < 256) colors.Add(pixels[i] | pixels[i + 1] << 8 | pixels[i + 2] << 16 | pixels[i + 3] << 24);
        }
        if (nonTransparent < width * (long)height / 100 || colors.Count < 8) throw new InvalidOperationException("Capture is transparent, blank or unlaid-out.");
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(path);
        encoder.Save(stream);
        return new { file = Path.GetFileName(path), widthPixels = width, heightPixels = height, dpi = 96 * scale, nonTransparentPixels = nonTransparent, nonTransparentFraction = nonTransparent / (double)(width * (long)height), distinctColorsCappedAt256 = colors.Count };
    }

    private static void Layout(FrameworkElement root, double width, double height)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0) throw new InvalidOperationException("Root has no finite layout size.");
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();
    }

    private static void Drain() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static IEnumerable<DependencyObject> Walk(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (DependencyObject child in Walk(VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    private static IEnumerable<UiNode> Flatten(IEnumerable<UiNode> nodes)
    {
        foreach (UiNode node in nodes)
        {
            yield return node;
            IEnumerable<UiNode> children = node switch { UiPanelNode panel => panel.Children, UiExpanderNode expander => expander.Children, _ => [] };
            foreach (UiNode child in Flatten(children)) yield return child;
        }
    }

    private static FieldInfo PrivateField(Type type, string name) => type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new MissingFieldException(type.FullName, name);

    private static string? Option(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index < 0 ? null : index + 1 < args.Length ? args[index + 1] : throw new ArgumentException("Missing value for " + name);
    }

    private static string FindDuComRoot()
    {
        foreach (string start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
            for (DirectoryInfo? directory = new(start); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "src", "DuCom", "DuCom.csproj"))) return directory.FullName;
                string nested = Path.Combine(directory.FullName, "DuCom");
                if (File.Exists(Path.Combine(nested, "src", "DuCom", "DuCom.csproj"))) return nested;
            }
        throw new DirectoryNotFoundException("Run inside the DuCom checkout, or from the tool's build output.");
    }

    // Application queues Startup even without Run when its dispatcher is pumped.
    // Deliberately do not call the product hooks: no composition root/workers/devices.
    private sealed class ReviewApp : DuCom.App
    {
        protected override void OnStartup(StartupEventArgs e) { }
        protected override void OnExit(ExitEventArgs e) { }
    }

    private sealed record CommandRecord(string CommandId, Dictionary<string, string> Values);
}
