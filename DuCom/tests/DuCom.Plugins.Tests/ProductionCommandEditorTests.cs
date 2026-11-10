using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DuCom.Plugins.Production;
using DuCom.Services.Plugins;
using Xunit;

namespace DuCom.Plugins.Tests;

public sealed class ProductionCommandEditorTests
{
    [Fact]
    public void ConfiguredMacCommandShowsFieldRowAndAutoExample() => WpfTestDispatcher.Run(() =>
    {
        using PluginUiTestScope scope = new();
        ProductionCommand command = ProductionProfile.CreateExample().Commands[0];
        ProductionCommandEditor window = new(command, ProductionProfile.CreateExample().Protocol);
        scope.Track(window);
        try
        {
            window.Show(); Layout(window);
            DataGrid grid = Descendants<DataGrid>(window).Single();
            Assert.Single(grid.Items);
            ResponseField field = Assert.IsType<ResponseField>(grid.Items[0]);
            Assert.Equal("MAC", field.Name);
            Assert.Equal(6, field.Length);
            // Every command gets a built-in example reply with resolvable sample bytes.
            string reply = Input(window, "回复整帧示例").Text;
            Assert.False(string.IsNullOrWhiteSpace(reply));
            Assert.Contains("11:22:33:44:55:66", Input(window, "示例解析结果").Text);
            Assert.Equal("本机MAC", Input(window, "名称").Text);
            Assert.Equal("94 02", Input(window, "匹配 CMD / 内容 HEX").Text);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void ManualOffsetsAndFixedReplyLengthSurvivePreview() => WpfTestDispatcher.Run(() =>
    {
        using PluginUiTestScope scope = new();
        ProductionCommand command = new()
        {
            Name = "MAC", Hex = "94 01", ResponseMatch = "94 02", MatchOffset = 3,
            FixedResponseLength = 16,
            Fields = [new() { Name = "MAC", Type = FieldType.Mac, Offset = 8, Example = "11 22 33 44 55 66" }],
        };
        ProductionCommandEditor window = new(command, new()); scope.Track(window);
        try
        {
            window.Show(); Layout(window);
            Assert.Equal(8, command.Fields[0].Offset);
            Assert.Equal(16, command.FixedResponseLength);
            string response = Input(window, "回复整帧示例").Text;
            Assert.Contains("11 22 33 44 55 66", response);
            Assert.Equal(16, Hex.Parse(response).Length);
            Assert.Contains(Descendants<TextBlock>(window), t => t.Text.Contains("未发送串口", StringComparison.Ordinal));
        }
        finally { window.Close(); }
    });

    [Fact]
    public void DetailIsCompactAndFooterRemainsReachableAtMinimumSize() => WpfTestDispatcher.Run(() =>
    {
        using PluginUiTestScope scope = new();
        ProductionCommandEditor window = new(ProductionProfile.CreateExample().Commands[0], new()); scope.Track(window);
        try
        {
            window.Width = 840; window.Height = Math.Min(700, window.MaxHeight);
            window.Show(); Layout(window);
            foreach (string section in new[] { "命令与时间", "发送", "回复", "回复字段", "示例" })
                Assert.Contains(Descendants<TextBlock>(window), t => t.Text == section);
            Grid root = Assert.IsType<Grid>(window.Content);
            Button save = Descendants<Button>(window).Single(b => Equals(b.Content, "保存命令"));
            Point point = save.TranslatePoint(new Point(), root);
            Assert.InRange(point.Y + save.ActualHeight, 1, root.ActualHeight + 1);
            Assert.InRange(point.X, 0, root.ActualWidth);
            Assert.InRange(point.X + save.ActualWidth, 1, root.ActualWidth + 1);
            ScrollViewer scroll = Assert.IsType<ScrollViewer>(root.Children[0]);
            Assert.True(scroll.ScrollableHeight > 0);
            scroll.ScrollToBottom(); Layout(window);
            string? output = Environment.GetEnvironmentVariable("DUCOM_COMMAND_DETAIL_SCREENSHOTS");
            if (output is not null)
            {
                Directory.CreateDirectory(output);
                RenderTargetBitmap bitmap = new((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(root);
                PngBitmapEncoder png = new(); png.Frames.Add(BitmapFrame.Create(bitmap));
                using (FileStream file = File.Create(Path.Combine(output, "command-detail-minimum.png"))) png.Save(file);
                scroll.ScrollToTop(); Layout(window);
                RenderTargetBitmap top = new((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32); top.Render(root);
                PngBitmapEncoder topPng = new(); topPng.Frames.Add(BitmapFrame.Create(top));
                using FileStream topFile = File.Create(Path.Combine(output, "command-detail-top.png")); topPng.Save(topFile);
            }
        }
        finally { window.Close(); }
    });

    [Fact]
    public void OpeningCompoundEditorDoesNotEraseComparisonRules() => WpfTestDispatcher.Run(() =>
    {
        using PluginUiTestScope scope = new();
        ProductionCommand group = new()
        {
            Steps = [new() { Id = "a" }, new() { Id = "b" }],
            Comparisons = [new() { ActualStepId = "a", ExpectedStepId = "b" }],
        };
        ProductionCompoundEditor window = new(group, new()); scope.Track(window);
        try { Assert.Single(group.Comparisons); }
        finally { window.Close(); }
    });

    [Fact]
    public void MovingToNextCellDoesNotCrashDeferredFieldNormalizer() => WpfTestDispatcher.Run(() =>
    {
        using PluginUiTestScope scope = new();
        ProductionCommand command = new()
        {
            Name = "蓝牙名", Hex = "01",
            Fields = [new() { Name = "名称", Type = FieldType.Hex, Offset = 8, Length = 4 }],
        };
        ProductionCommandEditor window = new(command, new()); scope.Track(window);
        try
        {
            window.Show(); Layout(window);
            DataGrid grid = Descendants<DataGrid>(window).Single();
            IEditableCollectionView view = Assert.IsAssignableFrom<IEditableCollectionView>(grid.Items);
            grid.CurrentCell = new DataGridCellInfo(command.Fields[0], grid.Columns[0]);
            grid.BeginEdit();
            Assert.True(view.IsEditingItem);
            // Committing the cell queues the deferred normalizer exactly like a real
            // CellEditEnding; starting the next cell in the same row keeps the row edit
            // transaction open when that deferred call runs. It used to throw
            // InvalidOperationException ("Refresh ... during an EditItem transaction")
            // and the unhandled dispatcher exception shut the whole app down.
            grid.CommitEdit(DataGridEditingUnit.Cell, true);
            grid.BeginEdit();
            Layout(window); // pump the dispatcher so the deferred NormalizeEditedField runs
            Assert.True(view.IsEditingItem);
            grid.CommitEdit(DataGridEditingUnit.Row, true);
            Layout(window); // the RowEditEnding deferral refreshes cleanly here
        }
        finally { window.Close(); }
    });

    [Fact]
    public void TypeDropdownShowsEveryFieldTypeWithoutScrolling() => WpfTestDispatcher.Run(() =>
    {
        using PluginUiTestScope scope = new();
        ProductionCommand command = new()
        {
            Name = "蓝牙名", Hex = "01",
            Fields = [new() { Name = "名称", Type = FieldType.Str, Offset = 5 }],
        };
        ProductionCommandEditor window = new(command, new()); scope.Track(window);
        try
        {
            window.Show(); Layout(window);
            DataGrid grid = Descendants<DataGrid>(window).Single();
            grid.CurrentCell = new DataGridCellInfo(command.Fields[0], grid.Columns[1]);
            grid.BeginEdit();
            ComboBox combo = Descendants<ComboBox>(grid).Single();
            // WPF's default DataGridComboBoxColumn editing style paints the editing combo
            // white over the dark theme; the column must neutralize it.
            Assert.False(combo.IsEditable);
            Assert.Equal(Brushes.Transparent, combo.Background);
            combo.IsDropDownOpen = true; Layout(window);
            // Opening the dropdown auto-scrolls to the selected item; if the list needs
            // scrolling the first entry (Hex) ends up out of view and looks missing.
            Popup popup = (Popup)combo.Template.FindName("PART_Popup", combo)!;
            Assert.True(popup.IsOpen);
            ComboBoxItem[] items = Descendants<ComboBoxItem>(popup.Child).ToArray();
            Assert.Equal(Enum.GetValues<FieldType>().Length, items.Length);
            Assert.Equal(FieldType.Hex, Assert.IsType<FieldType>(((ComboBoxItem)items[0]).Content));
            ScrollViewer viewer = Descendants<ScrollViewer>(popup.Child).Single();
            Assert.True(viewer.ScrollableHeight <= 0, $"类型下拉需要滚动 {viewer.ScrollableHeight:0.#}px，首项会被滚出视野");
        }
        finally { window.Close(); }
    });

    private static TextBox Input(Window window, string name) => Descendants<TextBox>(window).Single(t => AutomationProperties.GetName(t) == name);
    private static void Layout(Window window)
    {
        FrameworkElement content = (FrameworkElement)window.Content;
        content.Measure(new Size(window.Width, window.Height)); content.Arrange(new Rect(0, 0, window.Width, window.Height)); content.UpdateLayout();
        DispatcherFrame frame = new(); window.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => frame.Continue = false); Dispatcher.PushFrame(frame);
        window.UpdateLayout();
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (T child in Descendants<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
}
