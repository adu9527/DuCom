using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DuCom.Plugin;
using DuCom.Plugin.Dto;
using DuCom.Plugins.Audio;
using DuCom.Services.Plugins;
using Xunit;

namespace DuCom.Plugins.Tests;

public sealed class AudioUiTests
{
    [Fact]
    public void NativeAudioWindowFitsDefaultAndMinimumSizes()
    {
        Exception? failure = null;
        Thread thread = new(() =>
        {
            try
            {
                Application app = Application.Current ?? new Application();
                app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary { Theme = Wpf.Ui.Appearance.ApplicationTheme.Dark });
                app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
                app.Resources["Brush.ShellSurface"] = new SolidColorBrush(Color.FromRgb(20, 22, 27));
                app.Resources["Brush.TextPrimary"] = Brushes.WhiteSmoke;
                app.Resources["Brush.TextSecondary"] = new SolidColorBrush(Color.FromRgb(185, 190, 200));
                app.Resources["Brush.PanelBorder"] = new SolidColorBrush(Color.FromRgb(55, 60, 68));
                AudioWindow window = new((_, _) => Task.FromResult(true), (_, _) => Task.FromResult(true));
                List<UiNode> nodes = [new UiTextNode { FieldId = "native-state", Text = JsonSerializer.Serialize(new
                {
                    Status = "已加载 318 个在线语音。", Error = false, Busy = false, Percent = 0,
                    Directory = @"C:\Work_Du\Project\software\DuCom\DuCom\artifacts\portable\Logs", LastFile = "",
                    Preferences = new AudioPreferences(), ImportRevision = 0, VoiceRevision = 1,
                }) }];
                nodes.AddRange(new[] { new SpeechVoice("zh-CN-XiaoxiaoNeural", "晓晓", "zh-CN", "Female"), new SpeechVoice("zh-CN-YunxiNeural", "云希", "zh-CN", "Male"), new SpeechVoice("en-US-AriaNeural", "Aria", "en-US", "Female") }
                    .Select(v => new UiTextNode { FieldId = "voice-item", Text = JsonSerializer.Serialize(v) }));
                typeof(AudioWindow).GetMethod("UpdateState", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [nodes]);
                window.Show();
                RadioButton light = (RadioButton)window.FindName("LightTheme");
                light.IsChecked = true;
                Assert.Equal(Color.FromRgb(243, 245, 249), ((SolidColorBrush)((Panel)window.Content).Background).Color);
                Assert.DoesNotContain(Descendants<TextBlock>((DependencyObject)window.Content), b => b.Text.Contains("让文字有声", StringComparison.Ordinal));
                ((RadioButton)window.FindName("DarkTheme")).IsChecked = true;
                TextBox text = Descendants<TextBox>((DependencyObject)window.Content).Single(t => t.AcceptsReturn);
                text.Text = "你好，欢迎使用 DuCom 语音合成。\n\n输入文本，选择语音，即可生成 MP3 音频和同步 SRT 字幕。\n\n支持在线按需试听，无需安装 Python 或 FFmpeg。";
                Capture(window, "audio-default-dark.png");
                FrameworkElement titleBar = (FrameworkElement)window.FindName("AudioTitleBar");
                FrameworkElement captions = (FrameworkElement)window.FindName("CaptionButtons");
                FrameworkElement pin = (FrameworkElement)window.FindName("PinInput");
                Assert.Equal(48, titleBar.ActualHeight);
                Assert.Equal(48, pin.ActualHeight);
                Point captionOrigin = captions.TranslatePoint(new Point(), titleBar);
                Assert.InRange(captionOrigin.Y, 0, 48);
                Assert.InRange(captionOrigin.Y + captions.ActualHeight, 0, titleBar.ActualHeight);
                Assert.DoesNotContain(Descendants<TextBlock>((DependencyObject)window.Content), b => b.Text.Contains("Powered by", StringComparison.Ordinal));
                Assert.Empty(Descendants<Expander>((DependencyObject)window.Content));
                window.Width = 800; window.Height = 720;
                Capture(window, "audio-minimum-dark.png");
                Assert.True(text.ActualHeight >= 160);
                foreach (Button button in Descendants<Button>((DependencyObject)window.Content).Where(b => b.Content is string s && s is "开始合成" or "试听"))
                {
                    Point origin = button.TranslatePoint(new Point(), (UIElement)window.Content);
                    Assert.InRange(origin.Y + button.ActualHeight, 0, 720);
                }
                window.Width = 1080; window.Height = 800;
                // 4K at common 200% / 150% scaling corresponds to these logical viewports.
                foreach (Size viewport in new[] { new Size(1920, 1080), new Size(2560, 1440) })
                {
                    window.Width = viewport.Width; window.Height = viewport.Height;
                    Capture(window, "audio-4k-" + viewport.Width + ".png");
                    Assert.True(text.ActualWidth > 400);
                    Assert.Equal(48, titleBar.ActualHeight);
                }
                typeof(AudioWindow).GetMethod("CloseFromHost", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, []);
            }
            catch (Exception e) { failure = e; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T value) yield return value;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (T child in Descendants<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static void Capture(Window window, string name)
    {
        FrameworkElement content = (FrameworkElement)window.Content;
        content.Measure(new Size(window.Width, window.Height)); content.Arrange(new Rect(0, 0, window.Width, window.Height)); content.UpdateLayout();
        string? root = Environment.GetEnvironmentVariable("DUCOM_AUDIO_SCREENSHOTS");
        if (root is null) return;
        Directory.CreateDirectory(root);
        RenderTargetBitmap bitmap = new((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
        PngBitmapEncoder encoder = new(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream file = File.Create(Path.Combine(root, name)); encoder.Save(file);
    }
}
