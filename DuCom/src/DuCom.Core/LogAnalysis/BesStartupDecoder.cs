using System.Globalization;
using System.Text.RegularExpressions;

namespace DuCom.Core.LogAnalysis;

public static partial class BesStartupDecoder
{
    public static IReadOnlyList<BluetoothHciAnalysis> DecodeAll(string text)
    {
        List<BluetoothHciAnalysis> analyses = [];
        AddMatch(analyses, ChipRegex(), text, "系统信息", "BES Chip", match => $"BES 芯片/固件目标：{match.Groups["value"].Value}");
        AddMatch(analyses, ChipRoleRegex(), text, "系统信息", "BES Chip Role", match => $"芯片镜像角色：{match.Groups["value"].Value}");
        AddMatch(analyses, MetalRegex(), text, "系统信息", "BES Metal ID", match => $"芯片硅版本编号：{match.Groups["value"].Value}");
        AddMatch(analyses, PackageRegex(), text, "系统信息", "BES Chip Package", match => $"芯片封装版本 {match.Groups["version"].Value}：{match.Groups["package"].Value}");
        AddMatch(analyses, FlashSizeRegex(), text, "Flash", "Flash Size", match => FlashSizeComment(match.Groups["value"].Value));
        AddMatch(analyses, FlashBaseRegex(), text, "Flash", "Flash Address", match => $"{(match.Groups["nc"].Success ? "NC Flash" : "Flash")} 基地址：{match.Groups["value"].Value}");
        AddMatch(analyses, FlashIdRegex(), text, "Flash", "Flash ID", match => $"NOR Flash 原始 ID：{match.Groups["value"].Value}（厂商/型号未在日志中解码）");
        AddMatch(analyses, BuildDateRegex(), text, "固件版本", "Firmware Build Date", match => $"固件构建时间：{match.Groups["value"].Value.Trim()}");
        AddMatch(analyses, RevisionRegex(), text, "固件版本", "Firmware Revision", match => $"固件分支/目标：{match.Groups["value"].Value.Trim()}");
        AddMatch(analyses, SoftwareVersionRegex(), text, "固件版本", "Software Version", match => $"软件版本：{match.Groups["value"].Value}");
        AddMatch(analyses, FirmwareVersionRegex(), text, "固件版本", "Firmware Version", match => $"固件版本：{match.Groups["value"].Value}");
        AddMatch(analyses, LocalNameRegex(), text, "蓝牙身份", "Bluetooth Local Name", match => $"蓝牙本地名称：{match.Groups["value"].Value.Trim()}" );
        AddMatch(analyses, FactoryNameRegex(), text, "蓝牙身份", "Bluetooth Factory Name", match => $"工厂区蓝牙名称：{match.Groups["value"].Value.Trim()}" );
        AddMatch(analyses, LocalAddressRegex(), text, "蓝牙身份", "Bluetooth Local Address", match => $"本机蓝牙地址：{match.Groups["value"].Value}（按日志顺序）");
        AddMatch(analyses, WifiMacRegex(), text, "蓝牙身份", "Factory MAC Address", match => $"工厂区 MAC 地址：{match.Groups["value"].Value}");
        AddMatch(analyses, DacLoadRegex(), text, "音频初始化", "DAC Calibration Load", match => $"加载 DAC 直流校准：音频框架 {Enabled(match.Groups["open"].Value)}，重启标志 {match.Groups["reboot"].Value}");
        AddMatch(analyses, DacResultRegex(), text, "音频初始化", "DAC Calibration Result", match => $"DAC 校准加载结果：success={match.Groups["success"].Value}，有效记录 {match.Groups["valid"].Value}，记录数 {match.Groups["count"].Value}");
        AddMatch(analyses, CodecSwitchRegex(), text, "音频初始化", "Codec Switching State", match => $"编解码器切换状态字段：{match.Groups["value"].Value}（枚举未确认）");
        AddFixed(analyses, text, "app_poweron_key_init", "按键", "Power-on Key Init", "开机按键路径初始化");
        AddFixed(analyses, text, "app_key_init", "按键", "Application Key Init", "应用按键模块初始化");
        AddFixed(analyses, text, "hal_gpiokey_open", "按键", "GPIO Key Init", "GPIO 按键硬件层已打开");
        AddFixed(analyses, text, "app_key_gui_modual_init", "按键", "Key UI Init", "按键 UI 模块初始化");
        AddFixed(analyses, text, "besui_tws_key_init", "按键", "TWS Key Init", "TWS 按键模块初始化");
        return analyses;
    }

    private static string FlashSizeComment(string value)
    {
        long bytes = long.Parse(value.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        string size = bytes % (1024 * 1024) == 0 ? $"{bytes / (1024 * 1024)} MiB" : $"{bytes / 1024d:N0} KiB";
        return $"Flash 容量：{size}（{value}）";
    }

    private static string Enabled(string value) => value == "1" ? "已打开" : value == "0" ? "未打开" : $"状态 {value}";

    private static void AddMatch(List<BluetoothHciAnalysis> analyses, Regex regex, string text, string module, string keyword, Func<Match, string> comment)
    {
        Match match = regex.Match(text);
        if (match.Success) analyses.Add(new BluetoothHciAnalysis(module, keyword, comment(match)));
    }

    private static void AddFixed(List<BluetoothHciAnalysis> analyses, string text, string pattern, string module, string keyword, string comment)
    {
        if (text.Contains(pattern, StringComparison.OrdinalIgnoreCase)) analyses.Add(new BluetoothHciAnalysis(module, keyword, comment));
    }

    [GeneratedRegex(@"^CHIP=(?<value>[A-Za-z0-9_+-]+)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)] private static partial Regex ChipRegex();
    [GeneratedRegex(@"^CHIP_ROLE=(?<value>[A-Za-z0-9_+-]+)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)] private static partial Regex ChipRoleRegex();
    [GeneratedRegex(@"\bMETAL_ID:\s*(?<value>\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)] private static partial Regex MetalRegex();
    [GeneratedRegex(@"\bCHIP_PACKAGE_VER=(?<version>\d+)\s+(?<package>\S+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)] private static partial Regex PackageRegex();
    [GeneratedRegex(@"^FLASH_SIZE=(?<value>0x[0-9A-F]+)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)] private static partial Regex FlashSizeRegex();
    [GeneratedRegex(@"^FLASH_(?<nc>NC_)?BASE=(?<value>0x[0-9A-F]+)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)] private static partial Regex FlashBaseRegex();
    [GeneratedRegex(@"\bFLASH_ID:\s*(?<value>[0-9A-F]{2}(?:-[0-9A-F]{2}){2})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)] private static partial Regex FlashIdRegex();
    [GeneratedRegex(@"^BUILD_DATE=(?<value>.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)] private static partial Regex BuildDateRegex();
    [GeneratedRegex(@"^REV_INFO=:(?<value>.*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)] private static partial Regex RevisionRegex();
    [GeneratedRegex(@"\bSW_VERSION=(?<value>\S+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)] private static partial Regex SoftwareVersionRegex();
    [GeneratedRegex(@"\b(?:The Firmware rev is|customer firmware version=)(?<value>\S+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)] private static partial Regex FirmwareVersionRegex();
    [GeneratedRegex(@"\b(?:localname|blename)=(?<value>.*?),\s*namelen=\d+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)] private static partial Regex LocalNameRegex();
    [GeneratedRegex(@"\bfactory_section_open sucess btname:(?<value>.*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)] private static partial Regex FactoryNameRegex();
    [GeneratedRegex(@"\blocal_addr:\s*(?<value>[0-9A-F*]{2}(?::[0-9A-F*]{1,2}){5})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)] private static partial Regex LocalAddressRegex();
    [GeneratedRegex(@"\buse mac from factory:\s*(?<value>[0-9A-F]{2}(?::[0-9A-F]{2}){5})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)] private static partial Regex WifiMacRegex();
    [GeneratedRegex(@"\bcodec_dac_dc_auto_load: start: open_af=(?<open>\d+), reboot=(?<reboot>\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)] private static partial Regex DacLoadRegex();
    [GeneratedRegex(@"\bcodec_dac_dc_load_calib_value: success=(?<success>\d+), valid=(?<valid>\d+), num=(?<count>\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)] private static partial Regex DacResultRegex();
    [GeneratedRegex(@"\bcodec_switching=(?<value>\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)] private static partial Regex CodecSwitchRegex();
}
