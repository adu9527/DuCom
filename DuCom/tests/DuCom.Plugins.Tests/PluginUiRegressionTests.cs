using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DuCom.Plugin;
using DuCom.Plugins.Production;
using DuCom.Services.Plugins;
using Xunit;

namespace DuCom.Plugins.Tests;

public sealed class PluginUiRegressionTests
{
    [Fact]
    public void BesMinimumIsClampedToShortWorkAreaAndContentStillScrolls() => RunSta(scope =>
    {
        PluginToolWindow window = new("internal.ducom.bes-download",
            new ToolPageContribution { ContributionId = "bes", Title = "BES" }, "BES", (_, _) => Task.FromResult(true));
        scope.Track(window);
        try
        {
            // Deterministic 600-DIP budget, independent of the agent's desktop resolution.
            typeof(PluginToolWindow).GetMethod("ApplyWorkAreaConstraints", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, [new Size(1024, 624)]);
            Assert.Equal(600, window.MaxHeight);
            Assert.Equal(600, window.MinHeight);
            Assert.InRange(window.Height, 1, 600);
            Assert.True(window.MinWidth <= window.MaxWidth);
            window.SetContent(Enumerable.Range(0, 40).Select(index => (UiNode)new UiLabelNode { Text = "BES field " + index }).ToArray());
            window.Show();
            Settle(window);
            Grid shell = Assert.IsType<Grid>(window.Content);
            ScrollViewer viewer = Assert.IsType<ScrollViewer>(shell.Children[1]);
            Assert.Equal(ScrollBarVisibility.Auto, viewer.VerticalScrollBarVisibility);
            Assert.Equal(ScrollBarVisibility.Auto, viewer.HorizontalScrollBarVisibility);
            Assert.True(viewer.ScrollableHeight > 0);
            viewer.ScrollToBottom();
            Settle(window);
            Assert.Equal(viewer.ScrollableHeight, viewer.VerticalOffset, 1);
            FrameworkElement footer = Assert.IsAssignableFrom<FrameworkElement>(shell.Children[^1]);
            Point origin = footer.TranslatePoint(new Point(), shell);
            Assert.InRange(origin.Y + footer.ActualHeight, 0, shell.ActualHeight + 1);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void CrcAlternatingRowsFollowLightAndDarkTheme() => RunSta(scope =>
    {
        CrcWindow window = new((_, _) => Task.FromResult(true));
        scope.Track(window);
        try
        {
            ResourceDictionary? colors = null;
            foreach (string theme in new[] { "Light", "Dark", "Light" })
            {
                colors = SwitchTheme(window, colors, theme);
                DataGrid table = (DataGrid)window.FindName("ModelTable");
                // Realize the table inside the collapsed-by-default model reference section.
                if (!window.IsVisible) window.Show();
                FindAncestor<Expander>(table).IsExpanded = true;
                table.ScrollIntoView(table.Items[1]);
                Settle(window);
                Assert.Equal(ColorOf(window.FindResource("Brush.PanelRaised")), ColorOf(table.AlternatingRowBackground));
                DataGridRow row = Assert.IsType<DataGridRow>(table.ItemContainerGenerator.ContainerFromIndex(1));
                Assert.Equal(ColorOf(table.AlternatingRowBackground), ColorOf(row.Background));
                Assert.True(Contrast(ColorOf(table.Foreground), ColorOf(row.Background)) >= 4.5);
            }
        }
        finally { window.Close(); }
    });

    [Fact]
    public void ChecksumSemanticColorsUpdateOnThemeSwitchWithoutStateRefresh() => RunSta(scope =>
    {
        ChecksumWindow window = new(_ => Task.FromResult(true));
        scope.Track(window);
        try
        {
            TextBox checksum = (TextBox)window.FindName("CrcText");
            ResourceDictionary? colors = null;
            foreach ((string state, string key) in new[]
            {
                ("idle", "Brush.TextMuted"), ("verified", "Brush.Accent"),
                ("legacy", "Brush.Warning"), ("error", "Brush.Danger"), ("busy", "Brush.TextMuted"),
            })
            {
                object model = window.DataContext;
                if (state == "busy") model.GetType().GetMethod("Begin")!.Invoke(model, ["sample.bin"]);
                else if (state == "error") model.GetType().GetMethod("Fail")!.Invoke(model, ["sample error"]);
                else if (state != "idle")
                    typeof(ChecksumWindow).GetMethod("UpdateState", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window,
                        [new UiNode[] { new UiTextNode { FieldId = "native-state", Text = JsonSerializer.Serialize(new
                        { Path = "sample.bin", Code = "30000ABC", Verified = state == "verified" }) } }]);
                foreach (string theme in new[] { "Dark", "Light", "Dark" })
                {
                    // No Apply/Notify between theme changes: DynamicResource must do the work.
                    colors = SwitchTheme(window, colors, theme);
                    Settle(window);
                    Assert.Equal(ColorOf(window.FindResource(key)), ColorOf(checksum.Foreground));
                    Assert.True(Contrast(ColorOf(checksum.Foreground), ColorOf(window.FindResource("Brush.PanelSurface"))) >= 4.5);
                }
            }
        }
        finally { window.Close(); }
    });

    [Fact]
    public void ProductionTimingCellsKeepFiveDigitsVisibleAndTwoWayValidation() => RunSta(scope =>
    {
        ProductionProfile profile = ProductionProfile.CreateExample();
        profile.Commands[0].DelayMs = profile.Commands[0].TimeoutMs = 30000;
        ProductionProfileEditor window = new(profile);
        scope.Track(window);
        try
        {
            SwitchTheme(window, null, "Light");
            window.Show();
            Settle(window);
            DataGrid table = Descendants<DataGrid>(window).Single();
            table.ScrollIntoView(profile.Commands[0]);
            Settle(window);
            foreach (TextBox input in Descendants<TextBox>(window).Where(box =>
                         box.DataContext == profile.Commands[0] && AutomationProperties.GetName(box) is "间隔 ms" or "超时 ms"))
            {
                Assert.Equal("30000", input.Text);
                Assert.Equal(new Thickness(0), input.Margin);
                Assert.Equal(32, input.MinHeight);
                Assert.Equal(32, input.ActualHeight);
                ScrollViewer host = Assert.IsType<ScrollViewer>(input.Template.FindName("PART_ContentHost", input));
                double textWidth = new FormattedText(input.Text, System.Globalization.CultureInfo.CurrentCulture,
                    input.FlowDirection, new Typeface(input.FontFamily, input.FontStyle, input.FontWeight, input.FontStretch),
                    input.FontSize, input.Foreground, VisualTreeHelper.GetDpi(input).PixelsPerDip).WidthIncludingTrailingWhitespace;
                Assert.True(host.ViewportWidth >= textWidth, $"Timing viewport {host.ViewportWidth} clips five digits ({textWidth}).");
            }
            TextBox delay = Descendants<TextBox>(window).First(box => AutomationProperties.GetName(box) == "间隔 ms");
            Assert.Equal(2, Descendants<TextBox>(window).Count(box => box.DataContext == profile.Commands[0] &&
                AutomationProperties.GetName(box) is "间隔 ms" or "超时 ms"));
            delay.Text = "30001";
            Assert.Equal(30001, profile.Commands[0].DelayMs);
            delay.Text = "not a number";
            Assert.True(Validation.GetHasError(delay));
            delay.Text = "30000";
            Assert.False(Validation.GetHasError(delay));
        }
        finally { window.Close(); }
    });

    private static ResourceDictionary SwitchTheme(Window window, ResourceDictionary? previous, string theme)
    {
        if (previous is not null) window.Resources.MergedDictionaries.Remove(previous);
        ResourceDictionary colors = new() { Source = new Uri($"/DuCom;component/Resources/DesignTokens.Colors.{theme}.xaml", UriKind.Relative) };
        window.Resources.MergedDictionaries.Add(colors);
        return colors;
    }

    private static Color ColorOf(object brush) => Assert.IsType<SolidColorBrush>(brush).Color;
    private static double Contrast(Color a, Color b)
    {
        static double Luminance(Color color)
        {
            static double Linear(byte value) { double s = value / 255d; return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
            return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
        }
        double x = Luminance(a), y = Luminance(b);
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }

    private static T FindAncestor<T>(DependencyObject element) where T : DependencyObject
    {
        for (DependencyObject? parent = LogicalTreeHelper.GetParent(element); parent is not null; parent = LogicalTreeHelper.GetParent(parent))
            if (parent is T match) return match;
        throw new InvalidOperationException($"No {typeof(T).Name} ancestor.");
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (T child in Descendants<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    private static void Settle(DispatcherObject element)
    {
        DispatcherFrame frame = new();
        element.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
        if (element is Window window && window.Content is FrameworkElement content)
        {
            content.Measure(new Size(window.Width, window.Height));
            content.Arrange(new Rect(0, 0, window.Width, window.Height));
            content.UpdateLayout();
        }
        else if (element is UIElement ui) ui.UpdateLayout();
    }

    private static void RunSta(Action<PluginUiTestScope> test)
    {
        Exception? failure = null;
        WpfTestDispatcher.Run(() =>
        {
            try
            {
                using PluginUiTestScope scope = new();
                test(scope);
            }
            catch (Exception exception) { failure = exception; }
        });
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
