using DuCom.Core.Parsing;
using DuCom.Core.Sending;

namespace DuCom.Services;

/// <summary>
/// Built-in data migrated from the maintained RS-20 SuperCom profile. It is only added
/// when a user has not already created an item with the same name.
/// </summary>
public static class DefaultDuComData
{
    public const string MyProjectName = "我的项目";

    public static IReadOnlyList<CommandGroup> MergeCommandGroups(
        IReadOnlyList<CommandGroup> groups,
        out bool changed)
    {
        List<CommandGroup> merged = [.. groups];
        changed = false;
        foreach (CommandGroup group in CreateCommandGroups())
        {
            if (merged.Any(existing => string.Equals(existing.Name, group.Name, StringComparison.Ordinal)))
            {
                continue;
            }

            merged.Add(group);
            changed = true;
        }

        return merged;
    }

    public static IReadOnlyList<HighlightFilterRule> MergeHighlightRules(
        IReadOnlyList<HighlightFilterRule> rules,
        out bool changed)
    {
        List<HighlightFilterRule> merged = [.. rules];
        changed = false;
        foreach (HighlightFilterRule rule in CreateHighlightRules())
        {
            if (merged.Any(existing => string.Equals(existing.Name, rule.Name, StringComparison.Ordinal)))
            {
                continue;
            }

            merged.Add(rule);
            changed = true;
        }

        return merged;
    }

    private static IReadOnlyList<CommandGroup> CreateCommandGroups() =>
    [
        // The legacy project intentionally starts empty and remains the default selection.
        CommandGroup.Create(MyProjectName),
        new CommandGroup(Guid.NewGuid(), "RS-20",
        [
            Command("进入测试模式", "besphy start", 1),
            Command("进入TX模式", "besphy tx", 2),
            Command("选择信道 36", "besphy channel 36", 3),
            Command("read 1", "besphy read 0x72c0b6d0", 4),
            Command("read 2", "besphy read 0x72c0b6d4", 5),
            Command("read 3", "besphy read 0x72c0b6d8", 6),
            Command("read 4", "besphy read 0x72c0b6dc", 7),
            Command("read 5", "besphy read 0x72c0b6e8", 8),
            Command("read 6", "besphy read 0x72c0b6ec", 9),
            Command("强制进入WIFI", "user_xplay_force_wifi_mode", 10),
            Command("重启", "reboot", 11),
            Command("read 7", "besphy read 0x4008600c", 12),
            Command("ANC OFF", "test_x_app, anc_off", 13),
            Command("ANC ON", "test_x_app, anc_on", 14),
            Command("WIFI OFF", "test_x_app, test_xplay_stop", 15),
            Command("打开 BT", "ui_open", 16),
            Command("打开 配对", "pairing_mode 1", 17),
            Command("dongle 配对", "test_x_app test_auto_pairing_master", 18),
            Command("耳机配对", "test_x_app test_auto_pairing_slave", 19),
            Command("设置 ID", "x_flash set dev_id 2", 20),
            Command("清除组队", "x_flash reset all", 21),
            Command("设置USB", "x_flash set m_src_type 1", 22),
            Command("设置地址12", "besphy set_addr 00:80:43:54:81:12", 23),
            Command("设置地址34", "besphy set_addr 00:80:46:55:81:34", 24),
            Command("设置输出DAC", "x_flash set s_out_type 1", 25),
            Command("关闭 BT", "ui_close", 26),
            Command("关闭蓝牙扫描", "bt_start_scan 0", 27),
            Command("关USB", "iusb_stop", 28),
            Command("开USB", "iusb_start", 29),
            Command("启动xplay", "x_app run", 30),
            Command("关闭xplay", "x_app stop", 31),
            Command("查看设备", "x_flash show", 32),
            Command("写Flash", "x_flash flush", 33),
            Command("查看WIFI地址", "get_mac 1", 34),
            Command("取消组网", "test_x_app test_xplay_app_api_cancel_pairing", 35),
            Command("用户自定义组网", "user_xplay_pairing", 36),
            Command("耳机写dongle地址", "user_xplay_bind_earphone 00:80:43:55:81:13", 37),
            Command("dongle写WIFI地址", "user_xplay_bind_dongle 00:80:43:55:81:35", 38),
            Command("新SDK关蓝牙", "BTA_TEST close", 39),
        ]),
    ];

    private static ScriptCommand Command(string name, string payload, int order) =>
        ScriptCommand.Create(name, payload, order) with { DelayMilliseconds = 200 };

    private static IReadOnlyList<HighlightFilterRule> CreateHighlightRules() =>
    [
        Highlight("BES / Error", @"\b(?:ERROR|ERR|FATAL|ASSERT|PANIC|EXCEPTION|FAIL|FAILED|bybye|shutdown)\b", 255, 85, 85, true),
        Highlight("BES / Warning", @"\b(?:WARN|WARNING|UNDERRUN|OVERRUN)\b", 255, 215, 0, true),
        Highlight("BES / Status", @"\b(?:INFO|DEBUG|TRACE|RUNNING|SUCCESS|CONNECTED|DISCONNECTED)\b", 102, 204, 255),
        Highlight("BES / Fault", @"\b(?:WATCHDOG|CRASH|FAULT|HARDFAULT|STACK_OVERFLOW|HEAP_OVERFLOW|PLUGOUT)\b", 255, 140, 0, true),
        Highlight("BES / Audio", @"\b(?:ANC|CODEC|SBC|AAC|LDAC|LHDC|LC3|A2DP|SCO)\b", 124, 252, 0, true),
        Highlight("BES / Address", @"0x[0-9A-Fa-f]+", 0, 206, 209),
        Highlight("BES / Unit", @"\b\d+(?:\.\d+)?\s*(?:ms|us|Hz|kHz|MHz|dB|dBm|mV|mA|KB|MB|%)\b", 152, 251, 152),
        Highlight("BES / Version", "BES_v2", 51, 51, 51),
    ];

    private static HighlightFilterRule Highlight(string name, string pattern, byte red, byte green, byte blue, bool bold = false) =>
        new(Guid.NewGuid(), name, HighlightFilterRuleKind.Highlight, RuleMatchMode.Regex, pattern, false, true,
            red, green, blue, null, null, null, bold);
}
