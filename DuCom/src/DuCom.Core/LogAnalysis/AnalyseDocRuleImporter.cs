using System.Xml.Linq;

namespace DuCom.Core.LogAnalysis;

public static class AnalyseDocRuleImporter
{
    private static readonly Dictionary<string, (byte R, byte G, byte B)> Colors =
        new Dictionary<string, (byte, byte, byte)>(StringComparer.OrdinalIgnoreCase)
        {
            ["red"] = (220, 38, 38),
            ["green"] = (22, 163, 74),
            ["blue"] = (37, 99, 235),
            ["pink"] = (236, 72, 153),
            ["cyan"] = (6, 182, 212),
            ["yellow"] = (250, 204, 21),
            ["liteGreen"] = (134, 239, 172),
            ["liteBlue"] = (125, 211, 252),
            ["litePink"] = (249, 168, 212),
            ["veryLiteBlue"] = (224, 242, 254),
            ["veryLitePurple"] = (243, 232, 255),
        };

    public static IReadOnlyList<LogAnalyzerRule> Import(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        XDocument document = XDocument.Load(stream, LoadOptions.None);
        return document.Root?.Elements("SearchText")
            .Select(ToRule)
            .Where(rule => !string.IsNullOrEmpty(rule.Pattern))
            .ToArray() ?? [];
    }

    private static LogAnalyzerRule ToRule(XElement element)
    {
        string pattern = element.Value;
        string name = (string?)element.Attribute("comment") ?? pattern;
        string comment = CommentFor(pattern, (string?)element.Attribute("comment"));
        string foregroundName = (string?)element.Attribute("color") ?? string.Empty;
        string backgroundName = (string?)element.Attribute("bgColor") ?? string.Empty;
        bool includeInAnalysis = !string.Equals((string?)element.Attribute("doSearch"), "false", StringComparison.OrdinalIgnoreCase);
        (byte R, byte G, byte B)? foreground = Colors.TryGetValue(foregroundName, out var fg) ? fg : null;
        (byte R, byte G, byte B)? background = Colors.TryGetValue(backgroundName, out var bg) ? bg : null;

        return new LogAnalyzerRule(Guid.NewGuid(), name, comment, CategoryFor(pattern, backgroundName, name), pattern,
            IsCaseSensitive: false, IsEnabled: true,
            foreground?.R, foreground?.G, foreground?.B,
            background?.R, background?.G, background?.B,
            IncludeInAnalysis: includeInAnalysis);
    }

    private static string CategoryFor(string pattern, string background, string sourceComment = "") => pattern switch
    {
        var value when value.Contains("ASSERT", StringComparison.OrdinalIgnoreCase) ||
                       value.Contains("EXCEPTION", StringComparison.OrdinalIgnoreCase) ||
                       value.Contains("WARNING", StringComparison.OrdinalIgnoreCase) => "异常",
        var value when value.Contains("A2DP", StringComparison.OrdinalIgnoreCase) ||
                       value.Contains("HF", StringComparison.OrdinalIgnoreCase) ||
                       value.Contains("player", StringComparison.OrdinalIgnoreCase) ||
                       value.Contains("aud", StringComparison.OrdinalIgnoreCase) => "音频",
        var value when value.Contains("BOX", StringComparison.OrdinalIgnoreCase) ||
                       value.Contains("charger", StringComparison.OrdinalIgnoreCase) ||
                       value.Contains("WEAR", StringComparison.OrdinalIgnoreCase) => "盒仓与佩戴",
        var value when value.Contains("CONNECT", StringComparison.OrdinalIgnoreCase) ||
                       value.Contains("DISCONNECT", StringComparison.OrdinalIgnoreCase) ||
                       value.Contains("ibrt", StringComparison.OrdinalIgnoreCase) ||
                       value.Contains("avrcp", StringComparison.OrdinalIgnoreCase) ||
                       sourceComment.Contains("Connection", StringComparison.OrdinalIgnoreCase) ||
                       sourceComment.Contains("accessible", StringComparison.OrdinalIgnoreCase) ||
                       value.StartsWith("01 ", StringComparison.OrdinalIgnoreCase) ||
                       value.StartsWith("04 ", StringComparison.OrdinalIgnoreCase) => "蓝牙连接",
        _ when string.Equals(background, "cyan", StringComparison.OrdinalIgnoreCase) => "流程",
        _ => "其他",
    };

    private static string CommentFor(string pattern, string? sourceComment)
    {
        if (!string.IsNullOrWhiteSpace(sourceComment))
        {
            return sourceComment switch
            {
                "Connection Complete event" => "连接完成事件",
                "Connection Request event" => "连接请求事件",
                "Disconnection Complete event" => "断开连接完成事件",
                "Create Connection command" => "创建连接命令",
                "accessible_change" => "设置蓝牙可连接/可发现状态",
                "set addr" => "设置蓝牙地址",
                _ => sourceComment,
            };
        }

        return pattern switch
        {
            "METAL_ID" => "固件或芯片标识",
            "app_deinit" => "应用反初始化",
            "zk_chargerbox_shutdown" => "充电盒关机",
            "ASSERT" => "断言异常",
            "EXCEPTION" => "系统异常",
            "af_thread:WARNING" => "音频框架线程警告",
            "::HF" => "免提通话链路",
            "::A2DP" => "蓝牙音乐链路",
            "::avrcp" => "蓝牙媒体控制",
            "status changed" => "状态发生变化",
            "[Notify]" => "通知事件",
            "avrcp_key" => "媒体控制按键",
            "CONNECT_IND/CNF" => "连接指示或确认",
            "[BTEVENT] DISCONNECT" => "蓝牙断开事件",
            "app_bt_accessmode_set" => "设置蓝牙可访问模式",
            "QLOG" => "关键流程日志",
            "communication" => "通信过程",
            "uart_rx_idle" => "串口接收空闲",
            "IN BOX!" => "耳机放入盒内",
            "OUT BOX!" => "耳机移出盒外",
            "BOX CLOSE!" => "盒盖关闭",
            "BOX OPEN!" => "盒盖打开",
            "WEAR UP!" => "检测到佩戴",
            "WEAR DOWN!" => "检测到摘下",
            "curr_active_media" => "当前活动媒体类型",
            "media_active" => "媒体活动状态",
            "bt_media_start" => "蓝牙媒体开始",
            "bt_media_stop" => "蓝牙媒体停止",
            "app_ble_force_switch_adv" => "强制切换 BLE 广播",
            "app_voice_report_handler" => "语音提示处理",
            "zk_chargerbox_open_box" => "充电盒开盖",
            "zk_chargerbox_close_box" => "充电盒关盖",
            "zk_chargerbox_pair" => "充电盒触发配对",
            "zk_chargerbox_set_pair_flag" => "设置充电盒配对标志",
            "zk_chargerbox_clear_pair" => "清除充电盒配对状态",
            "app_ipop_adv_start" => "开始广播",
            "app_ipod_adv_stop" => "停止广播",
            _ => CategoryFor(pattern, string.Empty) switch
            {
                "异常" => "异常相关事件",
                "音频" => "音频相关事件",
                "盒仓与佩戴" => "盒仓或佩戴状态事件",
                "连接与 IBRT" => "蓝牙连接或 IBRT 流程",
                _ => "匹配关键词：" + pattern.Trim(),
            },
        };
    }
}
