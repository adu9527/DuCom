using System.Globalization;
using System.Windows.Data;

namespace DuCom.Converters;

/// <summary>Localized display labels only; unknown protocol IDs remain intact.</summary>
public sealed class PluginStringLabelConverter : IValueConverter
{
    private static readonly Dictionary<string, (string Chinese, string English)> Labels = new(StringComparer.Ordinal)
    {
        ["menu"] = ("菜单", "Menu"),
        ["tool-page"] = ("工具页面", "Tool page"),
        ["settings-panel"] = ("设置面板", "Settings panel"),
        ["background-image"] = ("背景图", "Background image"),
        ["storage.own"] = ("私有存储", "Private storage"),
        ["serial.read"] = ("串口读取", "Read serial data"),
        ["serial.logs.read"] = ("日志读取", "Read serial logs"),
        ["serial.lease"] = ("串口租用", "Lease serial ports"),
        ["files.user-selected.read"] = ("读取所选文件", "Read selected files"),
        ["files.user-selected.write"] = ("写入所选文件", "Write selected files"),
        ["native-helpers.execute"] = ("运行本地程序（完整用户权限，需额外确认）", "Run native programs (full user authority; extra approval)"),
        ["native-hosts.execute"] = ("启动本地辅助程序", "Run native hosts"),
        ["speech.online"] = ("在线语音", "Online speech"),
    };

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string id && Labels.TryGetValue(id, out var label))
            return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh" ? label.Chinese : label.English;
        return value?.ToString() ?? string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
