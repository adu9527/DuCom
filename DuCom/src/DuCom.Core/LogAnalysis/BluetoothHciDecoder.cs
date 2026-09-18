using System.Globalization;
using System.Text.RegularExpressions;

namespace DuCom.Core.LogAnalysis;

public sealed record BluetoothHciAnalysis(string Module, string Keyword, string Comment);

public static partial class BluetoothHciDecoder
{
    private static readonly Dictionary<ushort, (string Keyword, string Comment)> Commands =
        new Dictionary<ushort, (string, string)>
        {
            [0x0401] = ("HCI Inquiry", "开始搜索附近蓝牙设备"),
            [0x0402] = ("HCI Inquiry Cancel", "停止搜索附近蓝牙设备"),
            [0x0405] = ("HCI Create Connection", "创建蓝牙连接"),
            [0x0406] = ("HCI Disconnect", "断开蓝牙连接"),
            [0x0409] = ("HCI Accept Connection Request", "接受蓝牙连接请求"),
            [0x040A] = ("HCI Reject Connection Request", "拒绝蓝牙连接请求"),
            [0x0411] = ("HCI Authentication Requested", "请求蓝牙身份认证"),
            [0x0413] = ("HCI Set Connection Encryption", "设置蓝牙连接加密"),
            [0x0419] = ("HCI Remote Name Request", "读取远端蓝牙设备名称"),
            [0x041B] = ("HCI Read Remote Supported Features", "读取远端蓝牙设备支持的功能"),
            [0x041D] = ("HCI Read Remote Version Information", "读取远端蓝牙版本信息"),
            [0x080B] = ("HCI Switch Role", "请求切换蓝牙连接角色"),
            [0x080F] = ("HCI Write Link Policy Settings", "设置蓝牙连接策略"),
            [0x0C01] = ("HCI Set Event Mask", "设置 HCI 事件掩码"),
            [0x0C03] = ("HCI Reset", "复位蓝牙控制器"),
            [0x0C13] = ("HCI Write Local Name", "设置本机蓝牙名称"),
            [0x0C1A] = ("HCI Write Scan Enable", "设置蓝牙可连接/可发现状态"),
            [0x0C1C] = ("HCI Write Page Scan Activity", "设置蓝牙连接扫描参数"),
            [0x0C1E] = ("HCI Write Inquiry Scan Activity", "设置蓝牙发现扫描参数"),
            [0x0C24] = ("HCI Write Class of Device", "设置蓝牙设备类型"),
            [0x0C3A] = ("HCI Write Current IAC LAP", "设置蓝牙可发现模式"),
            [0x2006] = ("HCI LE Set Advertising Parameters", "设置 BLE 广播参数"),
            [0x2007] = ("HCI LE Read Advertising Channel Tx Power", "读取 BLE 广播发射功率"),
            [0x2008] = ("HCI LE Set Advertising Data", "设置 BLE 广播数据"),
            [0x2009] = ("HCI LE Set Scan Response Data", "设置 BLE 扫描响应数据"),
            [0x200A] = ("HCI LE Set Advertising Enable", "设置 BLE 广播状态"),
            [0x200B] = ("HCI LE Set Scan Parameters", "设置 BLE 扫描参数"),
            [0x200C] = ("HCI LE Set Scan Enable", "设置 BLE 扫描状态"),
            [0x200D] = ("HCI LE Create Connection", "创建 BLE 连接"),
            [0x200E] = ("HCI LE Create Connection Cancel", "取消创建 BLE 连接"),
            [0x200F] = ("HCI LE Read Filter Accept List Size", "读取 BLE 过滤接受列表容量"),
            [0x2010] = ("HCI LE Clear Filter Accept List", "清空 BLE 过滤接受列表"),
            [0x2011] = ("HCI LE Add Device To Filter Accept List", "向 BLE 过滤接受列表添加设备"),
            [0x2012] = ("HCI LE Remove Device From Filter Accept List", "从 BLE 过滤接受列表移除设备"),
            [0x2013] = ("HCI LE Connection Update", "更新 BLE 连接参数"),
            [0x2015] = ("HCI LE Read Channel Map", "读取 BLE 连接信道映射"),
            [0x2016] = ("HCI LE Read Remote Features", "读取远端 BLE 功能"),
            [0x2019] = ("HCI LE Enable Encryption", "启动 BLE 连接加密"),
            [0x201A] = ("HCI LE Long Term Key Request Reply", "回复 BLE 长期密钥请求"),
            [0x201B] = ("HCI LE Long Term Key Request Negative Reply", "拒绝 BLE 长期密钥请求"),
            [0x2022] = ("HCI LE Set Data Length", "设置 BLE 数据长度"),
            [0x2031] = ("HCI LE Set Default PHY", "设置 BLE 默认 PHY"),
            [0x2032] = ("HCI LE Set PHY", "设置 BLE 连接 PHY"),
        };

    private static readonly Dictionary<byte, (string Keyword, string Comment)> Events =
        new Dictionary<byte, (string, string)>
        {
            [0x01] = ("HCI Inquiry Complete", "蓝牙设备搜索完成"),
            [0x02] = ("HCI Inquiry Result", "发现蓝牙设备"),
            [0x03] = ("HCI Connection Complete", "蓝牙连接完成"),
            [0x04] = ("HCI Connection Request", "收到蓝牙连接请求"),
            [0x05] = ("HCI Disconnection Complete", "蓝牙断开完成"),
            [0x06] = ("HCI Authentication Complete", "蓝牙身份认证完成"),
            [0x08] = ("HCI Encryption Change", "蓝牙连接加密状态变化"),
            [0x0E] = ("HCI Command Complete", "蓝牙命令执行完成"),
            [0x0F] = ("HCI Command Status", "蓝牙命令状态返回"),
            [0x12] = ("HCI Role Change", "蓝牙连接角色发生变化"),
            [0x13] = ("HCI Number Of Completed Packets", "蓝牙数据包发送完成"),
            [0x16] = ("HCI PIN Code Request", "请求蓝牙 PIN 码"),
            [0x17] = ("HCI Link Key Request", "请求蓝牙链路密钥"),
            [0x18] = ("HCI Link Key Notification", "蓝牙链路密钥已更新"),
            [0x2F] = ("HCI Extended Inquiry Result", "发现蓝牙设备（扩展信息）"),
            [0x3E] = ("HCI LE Meta Event", "BLE 控制器事件"),
        };

    private static readonly Dictionary<byte, (string Keyword, string Comment)> LeMetaEvents =
        new Dictionary<byte, (string, string)>
        {
            [0x01] = ("HCI LE Connection Complete", "BLE 连接完成"),
            [0x02] = ("HCI LE Advertising Report", "收到 BLE 广播报告"),
            [0x03] = ("HCI LE Connection Update Complete", "BLE 连接参数更新完成"),
            [0x04] = ("HCI LE Read Remote Features Complete", "读取远端 BLE 功能完成"),
            [0x05] = ("HCI LE Long Term Key Request", "收到 BLE 长期密钥请求"),
            [0x06] = ("HCI LE Remote Connection Parameter Request", "收到远端 BLE 连接参数请求"),
            [0x07] = ("HCI LE Data Length Change", "BLE 数据长度发生变化"),
            [0x0A] = ("HCI LE Enhanced Connection Complete", "BLE 增强连接完成"),
            [0x0C] = ("HCI LE PHY Update Complete", "BLE PHY 更新完成"),
            [0x0D] = ("HCI LE Extended Advertising Report", "收到 BLE 扩展广播报告"),
            [0x12] = ("HCI LE Advertising Set Terminated", "BLE 广播集已终止"),
            [0x13] = ("HCI LE Scan Request Received", "收到 BLE 扫描请求"),
            [0x14] = ("HCI LE Channel Selection Algorithm", "BLE 信道选择算法已确定"),
        };

    public static IReadOnlyList<BluetoothHciAnalysis> DecodeAll(string text)
    {
        Match packet = HciPacketRegex().Match(text);
        if (!packet.Success) return [];

        MatchCollection matches = HexByteRegex().Matches(packet.Groups["bytes"].Value);
        if (matches.Count < 3) return [];

        List<BluetoothHciAnalysis> analyses = [];
        for (int index = 0; index <= matches.Count - 3; index++)
        {
            byte packetType = Parse(matches[index].Value);
            if (packetType == 0x01 && TryDecodeCommand(matches, index, out BluetoothHciAnalysis? command))
            {
                AddDistinct(analyses, command);
                index += 3 + Parse(matches[index + 3].Value);
            }
            else if (packetType == 0x04 && TryDecodeEvent(matches, index, out BluetoothHciAnalysis? hciEvent))
            {
                AddDistinct(analyses, hciEvent);
                index += 2 + Parse(matches[index + 2].Value);
            }
        }
        return analyses;
    }

    private static void AddDistinct(List<BluetoothHciAnalysis> analyses, BluetoothHciAnalysis? analysis)
    {
        if (analysis is null || analyses.Any(existing =>
            string.Equals(existing.Module, analysis.Module, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(existing.Keyword, analysis.Keyword, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(existing.Comment, analysis.Comment, StringComparison.OrdinalIgnoreCase))) return;
        analyses.Add(analysis);
    }

    private static bool TryDecodeCommand(MatchCollection bytes, int start, out BluetoothHciAnalysis? analysis)
    {
        analysis = null;
        if (start + 3 >= bytes.Count) return false;
        ushort opcode = (ushort)(Parse(bytes[start + 1].Value) | Parse(bytes[start + 2].Value) << 8);
        int parameterLength = Parse(bytes[start + 3].Value);
        if (start + 4 + parameterLength > bytes.Count) return false;

        if (opcode == 0x0C1A && parameterLength >= 1)
        {
            byte value = Parse(bytes[start + 4].Value);
            string comment = value switch
            {
                0x00 => "关闭蓝牙可连接、关闭蓝牙可发现",
                0x01 => "关闭蓝牙可连接、打开蓝牙可发现",
                0x02 => "打开蓝牙可连接、关闭蓝牙可发现",
                0x03 => "打开蓝牙可连接、打开蓝牙可发现",
                _ => $"设置蓝牙可连接/可发现状态（未知值 0x{value:X2}）",
            };
            analysis = new BluetoothHciAnalysis("蓝牙连接", "HCI Write Scan Enable", comment);
            return true;
        }

        if (opcode is 0x200A or 0x200C && parameterLength >= 1)
        {
            byte enabled = Parse(bytes[start + 4].Value);
            string target = opcode == 0x200A ? "BLE 广播" : "BLE 扫描";
            analysis = new BluetoothHciAnalysis("蓝牙连接", Commands[opcode].Keyword,
                enabled == 0 ? $"关闭{target}" : enabled == 1 ? $"打开{target}" : $"设置{target}状态（未知值 0x{enabled:X2}）");
            return true;
        }

        if (!Commands.TryGetValue(opcode, out (string Keyword, string Comment) known)) return false;
        analysis = new BluetoothHciAnalysis("蓝牙连接", known.Keyword, known.Comment);
        return true;
    }

    private static bool TryDecodeEvent(MatchCollection bytes, int start, out BluetoothHciAnalysis? analysis)
    {
        analysis = null;
        byte eventCode = Parse(bytes[start + 1].Value);
        int parameterLength = Parse(bytes[start + 2].Value);
        if (start + 3 + parameterLength > bytes.Count || !Events.TryGetValue(eventCode, out (string Keyword, string Comment) known)) return false;

        if (eventCode == 0x3E && parameterLength >= 1)
        {
            byte subeventCode = Parse(bytes[start + 3].Value);
            if (LeMetaEvents.TryGetValue(subeventCode, out (string Keyword, string Comment) leEvent))
            {
                string leComment = leEvent.Comment;
                if (subeventCode is 0x01 or 0x03 or 0x04 or 0x0A or 0x0C && parameterLength >= 2)
                {
                    byte status = Parse(bytes[start + 4].Value);
                    if (status != 0) leComment += $"，状态 0x{status:X2}";
                }
                analysis = new BluetoothHciAnalysis("低功耗蓝牙", leEvent.Keyword, leComment);
                return true;
            }
        }

        if (eventCode == 0x0E && parameterLength >= 3)
        {
            ushort opcode = (ushort)(Parse(bytes[start + 4].Value) | Parse(bytes[start + 5].Value) << 8);
            byte? status = parameterLength >= 4 ? Parse(bytes[start + 6].Value) : null;
            bool vendorSpecific = (opcode >> 10) == 0x3F;
            string commandName = Commands.TryGetValue(opcode, out (string Keyword, string Comment) command)
                ? command.Keyword
                : vendorSpecific ? $"BES Vendor Specific 0x{opcode:X4}" : $"HCI Opcode 0x{opcode:X4}";
            string result = status switch
            {
                null => "已返回完成事件（无状态字段）",
                0 => "执行成功",
                _ => $"执行失败，状态 {StatusName(status.Value)}",
            };
            analysis = new BluetoothHciAnalysis(vendorSpecific ? "BES HCI" : "蓝牙连接",
                $"HCI Command Complete: {commandName}", $"{commandName}：{result}");
            return true;
        }

        string comment = known.Comment;
        if (eventCode is 0x03 or 0x05 or 0x06 && parameterLength >= 1)
        {
            byte status = Parse(bytes[start + 3].Value);
            if (status != 0) comment += $"，状态 0x{status:X2}";
        }
        analysis = new BluetoothHciAnalysis("蓝牙连接", known.Keyword, comment);
        return true;
    }

    private static byte Parse(string value) => byte.Parse(
        value.AsSpan(value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? 2 : 0),
        NumberStyles.HexNumber,
        CultureInfo.InvariantCulture);

    private static string StatusName(byte status) => status switch
    {
        0x01 => "0x01 未知 HCI 命令",
        0x02 => "0x02 未知连接标识符",
        0x05 => "0x05 认证失败",
        0x0C => "0x0C 命令不允许",
        0x12 => "0x12 无效 HCI 命令参数",
        0x1F => "0x1F 未指定错误",
        _ => $"0x{status:X2}",
    };

    [GeneratedRegex(@"(?<![0-9A-Fa-f])(?:0x)?[0-9A-Fa-f]{2}(?![0-9A-Fa-f])", RegexOptions.CultureInvariant, 100)]
    private static partial Regex HexByteRegex();

    [GeneratedRegex(@"^(?:\[\d{2}:\d{2}:\d{2}\.\d{3}\]\s*)?(?:(?:HCI(?:\s+packet)?|hci_dump)\s*[:=-]?\s*)?(?<bytes>(?:0x)?(?:01|04)(?:\s+(?:0x)?[0-9A-Fa-f]{2}){2,})\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)]
    private static partial Regex HciPacketRegex();
}
