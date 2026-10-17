using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DuCom.Plugin;
using Xunit;

namespace DuCom.Plugins.Tests;

public sealed class ChecksumUiTests
{
    [Fact]
    public void OriginalWindowPreservesSizeLayoutDropAndCopyControls()
    {
        Exception? failure = null;
        Thread thread = new(() =>
        {
            try
            {
                Application app = Application.Current ?? new Application();
                app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary { Theme = Wpf.Ui.Appearance.ApplicationTheme.Dark });
                app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
                Type type = typeof(App).Assembly.GetType("DuCom.Services.Plugins.ChecksumWindow", true)!;
                string? selected = null;
                Func<string, Task<bool>> load = path => { selected = path; return Task.FromResult(true); };
                Window window = (Window)Activator.CreateInstance(type, load)!;
                Assert.Equal(700, window.Width);
                Assert.Equal(480, window.Height);
                Assert.Equal(600, window.MinWidth);
                Assert.Equal(460, window.MinHeight);
                Assert.True(window.AllowDrop);
                Assert.Equal(WindowStyle.None, window.WindowStyle);
                Assert.Contains(window.InputBindings.OfType<KeyBinding>(), key => key.Key == Key.O && key.Modifiers == ModifierKeys.Control);
                window.Show();
                Capture(window, "checksum-original-empty-700.png");
                string payload = JsonSerializer.Serialize(new { Busy = false, Error = false, Verified = false,
                    Path = @"C:\测试目录\合成界面示例.prd", Code = "DB27CCE3_B0FAB3A8", Name = "Synthetic Test Headset",
                    Chip = "AB571X（系列）", Manufacturer = "蓝讯", Date = "2026-10-17 12:00:00（文件修改时间）", FileType = "烧录固件" });
                type.GetMethod("UpdateState", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window,
                    [new UiNode[] { new UiTextNode { FieldId = "native-state", Text = payload, ReadOnly = true } }]);
                Capture(window, "checksum-original-loaded-700.png");
                TextBlock code = (TextBlock)window.FindName("CrcText");
                Assert.Equal("DB27CCE3_B0FAB3A8", code.Text);
                Assert.Equal(32, code.FontSize);
                Assert.Equal("AB571X（系列）", ((TextBlock)window.FindName("ChipText")).Text);
                Assert.Equal("复制", Descendants<TextBlock>((DependencyObject)window.FindName("CopyButton")).Last().Text);
                Assert.DoesNotContain(Descendants<TextBlock>((DependencyObject)window.Content), text => text.Text.Contains("支持恒玄", StringComparison.Ordinal) || text.Text.Contains("文件大小", StringComparison.Ordinal) || text.Text == "校验详情");
                window.Width = 600;
                window.Height = 460;
                Capture(window, "checksum-original-loaded-600.png");
                Border card = (Border)window.FindName("InformationCard");
                Assert.Equal(552, card.ActualWidth, 0);
                foreach (string name in new[] { "CrcText", "BluetoothNameText", "ChipText", "ManufacturerText", "DateText", "FileTypeText", "StatusText" })
                {
                    FrameworkElement element = (FrameworkElement)window.FindName(name);
                    Point origin = element.TranslatePoint(new Point(), (UIElement)window.Content);
                    Assert.InRange(origin.Y + element.ActualHeight, 0, 460);
                }
                string path = Path.GetTempFileName();
                try
                {
                    DataObject data = new(DataFormats.FileDrop, new[] { path });
                    DragEventArgs over = CreateDrag(data, window, DragDrop.PreviewDragOverEvent);
                    window.RaiseEvent(over);
                    Assert.Equal(DragDropEffects.Copy, over.Effects);
                    Capture(window, "checksum-original-drag-600.png");
                    DragEventArgs drop = CreateDrag(data, window, DragDrop.PreviewDropEvent);
                    window.RaiseEvent(drop);
                    Assert.Equal(path, selected);
                }
                finally { File.Delete(path); }
                window.Close();
                typeof(App).Assembly.GetType("DuCom.Services.Plugins.PluginIntegrationGuide", true)!
                    .GetMethod("Show", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [null]);
                Window guide = app.Windows.Cast<Window>().Single(item => item.Title.StartsWith("恒玄接入文档", StringComparison.Ordinal));
                guide.Close();
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T found) yield return found;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (T child in Descendants<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    private static DragEventArgs CreateDrag(IDataObject data, Window window, RoutedEvent routedEvent)
    {
        ConstructorInfo constructor = typeof(DragEventArgs).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)
            .Single(candidate => candidate.GetParameters().Length == 5);
        DragEventArgs args = (DragEventArgs)constructor.Invoke([data, DragDropKeyStates.None, DragDropEffects.Copy, window, new Point(100, 100)]);
        args.RoutedEvent = routedEvent;
        return args;
    }

    private static void Capture(Window window, string name)
    {
        FrameworkElement content = (FrameworkElement)window.Content;
        content.Measure(new Size(window.Width, window.Height));
        content.Arrange(new Rect(0, 0, window.Width, window.Height));
        content.UpdateLayout();
        RenderTargetBitmap bitmap = new((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        string? directory = Environment.GetEnvironmentVariable("DUCOM_CHECKSUM_SCREENSHOTS");
        if (directory is null) return;
        Directory.CreateDirectory(directory);
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream file = File.Create(Path.Combine(directory, name));
        encoder.Save(file);
    }
}
