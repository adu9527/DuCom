using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace DuCom.Services.Plugins;

/// <summary>Host-owned presentation for isolated declarative plugins; never loaded by plugin workers.</summary>
internal static class PluginVisualTheme
{
    internal static void Apply(FrameworkElement root)
    {
        if (!root.Resources.MergedDictionaries.Any(dictionary => dictionary.Source?.OriginalString.EndsWith("/PluginUiStyles.xaml", StringComparison.Ordinal) == true))
            root.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/DuCom;component/Services/Plugins/PluginUiStyles.xaml", UriKind.Relative) });
        root.UseLayoutRounding = true;
        // Pixel snapping is inherited. Keep rounded borders and vector icons antialiased.
        root.SnapsToDevicePixels = false;
        // Ideal 亚像素渲染：Display 模式把文字按像素网格对齐，是高分屏上“像素感”的直接来源。
        TextOptions.SetTextFormattingMode(root, TextFormattingMode.Ideal);
        TextOptions.SetTextRenderingMode(root, TextRenderingMode.ClearType);
        TextElement.SetFontFamily(root, new FontFamily("Segoe UI, Microsoft YaHei UI"));
        TextElement.SetFontWeight(root, FontWeights.Normal);
        // 跟随密度档位（1K/2K/4K）的基准字号，而不是写死的 14。
        // 插件工具窗统一在密度档位基础上加 2px：窗内控件多而密，基准字号比主窗大一号才协调。
        double bump = 2.0;
        double BaseOf(string key) => Application.Current?.Resources[key] as double? ?? 13.0;
        root.Resources["Fs.Body"] = BaseOf("Fs.Body") + bump;
        root.Resources["Fs.Small"] = BaseOf("Fs.Small") + bump;
        root.Resources["Fs.Xs"] = BaseOf("Fs.Xs") + bump;
        root.Resources["Fs.Title"] = BaseOf("Fs.Title") + bump;
        TextElement.SetFontSize(root, BaseOf("Fs.Body") + bump);
        if (root is Window window)
        {
            window.SetResourceReference(Control.BackgroundProperty, "Brush.ShellSurface");
            window.SetResourceReference(Control.ForegroundProperty, "Brush.TextPrimary");
        }
    }
}
