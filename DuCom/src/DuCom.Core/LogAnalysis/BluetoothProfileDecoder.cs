using System.Text.RegularExpressions;

namespace DuCom.Core.LogAnalysis;

public static partial class BluetoothProfileDecoder
{
    private static readonly Dictionary<string, (string Keyword, string Comment)> A2dpEvents =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["AVDTP_CONNECT"] = ("A2DP Connected", "A2DP 传输链路已连接"),
            ["CODEC_INFO"] = ("A2DP Codec", "A2DP 音乐编解码信息"),
            ["STREAM_OPEN"] = ("A2DP Stream Opened", "A2DP 音乐流已打开"),
            ["STREAM_STARTED"] = ("A2DP Stream Started", "A2DP 蓝牙音乐流开始"),
            ["STREAM_SUSPENDED"] = ("A2DP Stream Suspended", "A2DP 蓝牙音乐流暂停"),
            ["STREAM_CLOSED"] = ("A2DP Stream Stopped", "A2DP 蓝牙音乐流关闭"),
            ["DISCOVER_COMPLETE"] = ("A2DP Discovery Complete", "A2DP 能力发现完成"),
        };

    private static readonly Dictionary<string, (string Keyword, string Comment)> HfpEvents =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["SERVICE_CONNECTED"] = ("HFP Connected", "HFP 免提通话链路已连接"),
            ["SERVICE_DISCONNECTED"] = ("HFP Disconnected", "HFP 免提通话链路已断开"),
            ["AUDIO_CONNECTED"] = ("HFP Audio Connected", "HFP 通话音频链路已连接"),
            ["AUDIO_DISCONNECTED"] = ("HFP Audio Disconnected", "HFP 通话音频链路已断开"),
            ["CALL_IND"] = ("HFP Call State", "HFP 通话状态更新"),
            ["CALLSETUP_IND"] = ("HFP Call Setup State", "HFP 呼叫建立状态更新"),
            ["CALLHELD_IND"] = ("HFP Call Held State", "HFP 保持通话状态更新"),
        };

    private static readonly Dictionary<string, string> TwsStates = new(StringComparer.OrdinalIgnoreCase)
    {
        ["DISCONNECTED"] = "未连接",
        ["CONNECTING"] = "连接中",
        ["CONNECTED"] = "ACL 已连接",
        ["BESAUD-CONNECTING"] = "音频通道连接中",
        ["W4-SHAREINFO"] = "共享信息同步中",
    };

    public static IReadOnlyList<BluetoothHciAnalysis> DecodeAll(string text)
    {
        List<BluetoothHciAnalysis> analyses = [];
        DecodeProfileState(text, analyses);
        DecodeTwsState(text, analyses);
        DecodeA2dpEvent(text, analyses);
        DecodeA2dpState(text, analyses);
        DecodeAvrcpEvent(text, analyses);
        DecodeHfpEvent(text, analyses);
        DecodeRfcommEvent(text, analyses);
        DecodeBesBleRecord(text, analyses);
        DecodeBoxAndPower(text, analyses);
        DecodeStandardText(text, analyses);
        return analyses;
    }

    private static void DecodeProfileState(string text, List<BluetoothHciAnalysis> analyses)
    {
        Match header = ProfileStateHeaderRegex().Match(text);
        if (!header.Success) return;

        List<string> states = [];
        foreach (Match match in ProfileConnectionRegex().Matches(text))
        {
            string profile = match.Groups["profile"].Value.Equals("a2rvp", StringComparison.OrdinalIgnoreCase)
                ? "AVRCP"
                : match.Groups["profile"].Value.ToUpperInvariant();
            states.Add($"{profile}：连接标志 {match.Groups["connected"].Value}，状态码 {match.Groups["state"].Value}");
        }

        if (states.Count == 0) return;
        string role = header.Groups["role"].Value.Equals("MASTER", StringComparison.OrdinalIgnoreCase) ? "当前主机" : "当前侦听端";
        Add(analyses, "蓝牙配置状态", "Bluetooth Profile State",
            $"设备槽 d{header.Groups["device"].Value}，{role}；{string.Join("；", states)}");
    }

    private static void DecodeTwsState(string text, List<BluetoothHciAnalysis> analyses)
    {
        Match match = TwsStateRegex().Match(text);
        if (!match.Success) return;
        string rawState = match.Groups["state"].Value;
        string state = TwsStates.GetValueOrDefault(rawState, $"未知状态 {rawState}");
        Add(analyses, "IBRT/TWS", "TWS State", $"TWS 状态：{state}");
    }

    private static void DecodeA2dpEvent(string text, List<BluetoothHciAnalysis> analyses)
    {
        Match match = A2dpEventRegex().Match(text);
        if (!match.Success) return;
        string eventName = match.Groups["event"].Value;
        if (A2dpEvents.TryGetValue(eventName, out var known))
        {
            Add(analyses, "蓝牙音乐", known.Keyword, WithDevice(match, known.Comment));
        }
        else
        {
            Add(analyses, "蓝牙音乐", $"A2DP {eventName}", WithDevice(match, $"A2DP 事件 {eventName}"));
        }
    }

    private static void DecodeA2dpState(string text, List<BluetoothHciAnalysis> analyses)
    {
        Match current = A2dpCurrentStateRegex().Match(text);
        if (current.Success)
        {
            string playing = current.Groups["playing"].Value;
            string playingText = playing.Equals("ff", StringComparison.OrdinalIgnoreCase)
                ? "0xFF（未选择/无效哨兵，枚举未确认）"
                : $"索引 {Convert.ToInt32(playing, 16)}";
            Add(analyses, "蓝牙音乐", "A2DP Current Stream",
                $"A2DP 当前播放项：{playingText}；流 ID {current.Groups["stream"].Value}；内部字段 0x{current.Groups["internal"].Value.ToUpperInvariant()}");
        }

        if (!A2dpTupleHeaderRegex().IsMatch(text)) return;
        List<string> tuples = [];
        int index = 0;
        foreach (Match tuple in A2dpTupleRegex().Matches(text))
        {
            index++;
            string connected = tuple.Groups["connected"].Value == "1" ? "已连接" : "未连接";
            string streaming = tuple.Groups["streaming"].Value == "1" ? "正在传输" : "未传输";
            tuples.Add($"槽{index}：{connected}，状态码 {tuple.Groups["state"].Value}，{streaming}，播放状态码 {tuple.Groups["play"].Value}，AVRCP {tuple.Groups["avrcp"].Value}");
        }
        if (tuples.Count > 0) Add(analyses, "蓝牙音乐", "A2DP State Snapshot", string.Join("；", tuples));
    }

    private static void DecodeAvrcpEvent(string text, List<BluetoothHciAnalysis> analyses)
    {
        Match callback = AvrcpCallbackRegex().Match(text);
        if (callback.Success)
        {
            string eventName = callback.Groups["name"].Value;
            string comment = eventName.ToUpperInvariant() switch
            {
                "CONNECT" => "AVRCP 媒体控制链路已连接",
                "DISCONNECT" => "AVRCP 媒体控制链路已断开",
                "COMMAND" => "AVRCP 收到控制命令",
                "TXDONE" => "AVRCP 数据发送完成",
                "AVDANCED_NOTIFY" => "AVRCP 高级通知事件",
                "AVDANCED_RESPONSE" => "AVRCP 高级响应事件",
                "CT_SDP_INFO" => "AVRCP 控制端服务发现信息",
                _ => $"AVRCP 回调事件 {eventName}",
            };
            Add(analyses, "蓝牙媒体控制", $"AVRCP {eventName}", WithDevice(callback, comment));
        }

        Match playback = AvrcpPlaybackRegex().Match(text);
        if (playback.Success)
        {
            string value = playback.Groups["value"].Value;
            string state = value switch { "0" => "停止", "1" => "播放", "2" => "暂停", _ => $"未知状态 {value}" };
            Add(analyses, "蓝牙媒体控制", "AVRCP Playback Status", $"AVRCP 播放状态：{state}");
        }

        Match volume = AvrcpVolumeRegex().Match(text);
        if (volume.Success)
        {
            Add(analyses, "蓝牙媒体控制", "AVRCP Absolute Volume",
                $"AVRCP 设置绝对音量 {volume.Groups["raw"].Value}（{volume.Groups["percent"].Value}%）");
        }
    }

    private static void DecodeHfpEvent(string text, List<BluetoothHciAnalysis> analyses)
    {
        Match match = HfpEventRegex().Match(text);
        if (!match.Success) return;
        string eventName = match.Groups["event"].Value;
        (string Keyword, string Comment) known = HfpEvents.GetValueOrDefault(eventName,
            ($"HFP {eventName}", $"HFP 事件 {eventName}"));
        Add(analyses, "蓝牙通话", known.Keyword, WithDevice(match, known.Comment));

        Match tuple = HfpStateTupleRegex().Match(text);
        if (tuple.Success)
        {
            Add(analyses, "蓝牙通话", "HFP State Snapshot",
                $"HFP 状态：连接 {tuple.Groups["connected"].Value}，通话 {tuple.Groups["call"].Value}，呼叫建立 {tuple.Groups["setup"].Value}，保持 {tuple.Groups["held"].Value}，音频 {tuple.Groups["audio"].Value}");
        }
    }

    private static void DecodeRfcommEvent(string text, List<BluetoothHciAnalysis> analyses)
    {
        Match match = RfcommEventRegex().Match(text);
        if (!match.Success) return;
        string eventName = match.Groups["event"].Value.ToLowerInvariant();
        string details = match.Groups["details"].Value.Trim();
        (string Keyword, string Comment) result = eventName switch
        {
            "opened" => ("RFCOMM Channel Opened", "RFCOMM 串口通道已打开"),
            "closed" => ("RFCOMM Channel Closed", "RFCOMM 串口通道已关闭"),
            "connect_req" => ("RFCOMM Connection Request", "收到 RFCOMM 串口连接请求"),
            _ => ($"RFCOMM {eventName}", $"RFCOMM 事件 {eventName}"),
        };
        Add(analyses, "蓝牙串口", result.Keyword, string.IsNullOrEmpty(details) ? result.Comment : $"{result.Comment}（{details}）");
    }

    private static void DecodeBesBleRecord(string text, List<BluetoothHciAnalysis> analyses)
    {
        Match match = BesBleRecordRegex().Match(text);
        if (!match.Success) return;
        string subsystem = match.Groups["subsystem"].Value.ToUpperInvariant();
        string tag = match.Groups["tag"].Value.ToUpperInvariant();
        Add(analyses, "低功耗蓝牙", $"BES {subsystem} {tag}",
            $"BES {subsystem} 记录：代码 0x{match.Groups["code"].Value.ToUpperInvariant()}，标签 {tag}");
    }

    private static void DecodeBoxAndPower(string text, List<BluetoothHciAnalysis> analyses)
    {
        Match box = BoxEventRegex().Match(text);
        if (box.Success)
        {
            Add(analyses, "盒仓与佩戴", "Earbud Box State",
                $"盒仓状态由 {box.Groups["old"].Value} 变为 {box.Groups["input"].Value}");
        }

        Match charge = ChargeStateRegex().Match(text);
        if (charge.Success)
        {
            string value = charge.Groups["state"].Value;
            string comment = value switch
            {
                "CHARGE_PLUGIN" => "耳机接入充电",
                "CHARGE_PLUGOUT" => "耳机退出充电",
                _ => $"耳机充电插拔状态 {value}",
            };
            Add(analyses, "电池与充电", "Earbud Charge State", comment);
        }

        Match boot = BootReasonRegex().Match(text);
        if (boot.Success)
        {
            Add(analyses, "系统生命周期", "System Boot Reason", $"系统启动原因 {boot.Groups["reason"].Value}");
        }
    }

    private static void DecodeStandardText(string text, List<BluetoothHciAnalysis> analyses)
    {
        if (AvrcpKeyRegex().IsMatch(text))
        {
            if (Contains(text, "pause")) Add(analyses, "蓝牙媒体控制", "AVRCP Pause", "AVRCP 暂停播放");
            else if (Contains(text, "play")) Add(analyses, "蓝牙媒体控制", "AVRCP Play", "AVRCP 开始播放");
        }

        if (StandardAvrcpRegex().IsMatch(text))
        {
            if (AvrcpPauseRegex().IsMatch(text)) Add(analyses, "蓝牙媒体控制", "AVRCP Pause", "AVRCP 暂停播放");
            if (AvrcpPlayRegex().IsMatch(text)) Add(analyses, "蓝牙媒体控制", "AVRCP Play", "AVRCP 开始播放");
            if (AvrcpNotifyRegex().IsMatch(text)) Add(analyses, "蓝牙媒体控制", "AVRCP Notification", "AVRCP 状态通知");
        }

        if (HfpMarkerRegex().IsMatch(text))
        {
            if (AtCommandRegex(@"AT\+CHUP(?:\s|$)").IsMatch(text)) Add(analyses, "蓝牙通话", "HFP Hang Up", "HFP 挂断电话");
            if (AtCommandRegex(@"ATA(?:\s|$)").IsMatch(text)) Add(analyses, "蓝牙通话", "HFP Answer", "HFP 接听电话");
            if (ContainsAny(text, "+BAC", "+BCS", "codec negotiation")) Add(analyses, "蓝牙通话", "HFP Codec Negotiation", "HFP 通话音频编解码协商");
            if (ContainsAny(text, "sco connected", "audio connected")) Add(analyses, "蓝牙通话", "HFP Audio Connected", "HFP 通话音频链路已连接");
        }

        if (StandardRfcommRegex().IsMatch(text))
        {
            if (ContainsAny(text, "dlci open", "channel open")) Add(analyses, "蓝牙串口", "SPP Channel Open", "SPP RFCOMM 串口通道已打开");
            if (ContainsAny(text, "dlci close", "channel close")) Add(analyses, "蓝牙串口", "SPP Channel Closed", "SPP RFCOMM 串口通道已关闭");
        }

        if (StandardA2dpRegex().IsMatch(text))
        {
            if (ContainsAny(text, "stream start", "media start")) Add(analyses, "蓝牙音乐", "A2DP Stream Started", "A2DP 蓝牙音乐流开始");
            if (ContainsAny(text, "stream suspend", "media suspend")) Add(analyses, "蓝牙音乐", "A2DP Stream Suspended", "A2DP 蓝牙音乐流暂停");
            if (ContainsAny(text, "stream stop", "stream close", "media stop")) Add(analyses, "蓝牙音乐", "A2DP Stream Stopped", "A2DP 蓝牙音乐流停止");
            if (ContainsAny(text, " codec ", "codec:", "codec=")) Add(analyses, "蓝牙音乐", "A2DP Codec", "A2DP 音乐编解码配置发生变化");
        }

        if (StandardGattRegex().IsMatch(text) && Contains(text, "notification"))
            Add(analyses, "低功耗蓝牙", "GATT Notification", "GATT 特征通知");

        if (HciTextRegex().IsMatch(text))
        {
            if (Contains(text, "disconnection complete")) Add(analyses, "蓝牙连接", "HCI Disconnection Complete", "HCI 蓝牙断开完成");
            else if (Contains(text, "connection complete")) Add(analyses, "蓝牙连接", "HCI Connection Complete", "HCI 蓝牙连接完成");
            if (Contains(text, "command complete")) Add(analyses, "蓝牙连接", "HCI Command Complete", "HCI 命令执行完成");
            if (Contains(text, "command status")) Add(analyses, "蓝牙连接", "HCI Command Status", "HCI 命令状态返回");
        }

        if (TwsPageScanRegex().IsMatch(text))
            Add(analyses, "IBRT/TWS", "TWS Page Scan", "TWS 未连接，已开启 Page Scan 等待对耳连接");
    }

    private static string WithDevice(Match match, string comment) =>
        match.Groups["device"].Success ? $"设备槽 d{match.Groups["device"].Value}：{comment}" : comment;

    private static void Add(List<BluetoothHciAnalysis> analyses, string module, string keyword, string comment)
    {
        BluetoothHciAnalysis analysis = new(module, keyword, comment);
        if (!analyses.Contains(analysis)) analyses.Add(analysis);
    }

    private static bool Contains(string text, string value) => text.Contains(value, StringComparison.OrdinalIgnoreCase);
    private static bool ContainsAny(string text, params string[] values) => values.Any(value => Contains(text, value));
    private static Regex AtCommandRegex(string expression) => new(expression, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    [GeneratedRegex(@"(?i)\b(?<role>MASTER|SNOOP)\s+MOBILE\s+profile_state:\s*\[d(?<device>\d+)\]", RegexOptions.CultureInvariant, 100)]
    private static partial Regex ProfileStateHeaderRegex();

    [GeneratedRegex(@"(?i)\b(?<profile>a2rvp|avrcp|a2dp|hfp)\s+con(?:n)?\s*:\s*(?<connected>\d+)\s*,\s*(?<state>\d+)", RegexOptions.CultureInvariant, 100)]
    private static partial Regex ProfileConnectionRegex();

    [GeneratedRegex(@"(?i)\bTWS-STATE:(?<state>[A-Z0-9_-]+)\s+EVENT=(?<event>[A-Z0-9_]+)", RegexOptions.CultureInvariant, 100)]
    private static partial Regex TwsStateRegex();

    [GeneratedRegex(@"(?i)\btws mode and tws not connect,\s*enable pscan\b", RegexOptions.CultureInvariant, 100)]
    private static partial Regex TwsPageScanRegex();

    [GeneratedRegex(@"(?i)(?:\(d(?<device>\d+)\)\s*)?::A2DP_EVENT_(?<event>[A-Z0-9_]+)", RegexOptions.CultureInvariant, 100)]
    private static partial Regex A2dpEventRegex();

    [GeneratedRegex(@"(?i)\ba2dp_state:\s+curr_playing_a2dp\s+(?<playing>[0-9A-F]+)\s+curr_a2dp_stream_id\s+(?<stream>\d+)\s+int_a2dp\s+(?<internal>[0-9A-F]+)", RegexOptions.CultureInvariant, 100)]
    private static partial Regex A2dpCurrentStateRegex();

    [GeneratedRegex(@"(?i)\ba2dp_state:\s+\(conn\s+state\s+strming\s+playstat\s+avrcp\)=", RegexOptions.CultureInvariant, 100)]
    private static partial Regex A2dpTupleHeaderRegex();

    [GeneratedRegex(@"\((?<connected>\d+)\s+(?<state>\d+)\s+(?<streaming>\d+)\s+(?<play>\d+)\s+\S+\s+(?<avrcp>\d+)\)?", RegexOptions.CultureInvariant, 100)]
    private static partial Regex A2dpTupleRegex();

    [GeneratedRegex(@"(?i)(?:\(d(?<device>\d+)\)\s*)?::avrcp_callback_[A-Z]+.*?\bevent\s+\d+\[(?<name>[A-Z0-9_]+)\]", RegexOptions.CultureInvariant, 100)]
    private static partial Regex AvrcpCallbackRegex();

    [GeneratedRegex(@"(?i)playback_changed_status\s*=\s*(?<value>\d+)", RegexOptions.CultureInvariant, 100)]
    private static partial Regex AvrcpPlaybackRegex();

    [GeneratedRegex(@"(?i)set_absolute_volume\s+(?<raw>\d+)\s+(?<percent>\d+)%", RegexOptions.CultureInvariant, 100)]
    private static partial Regex AvrcpVolumeRegex();

    [GeneratedRegex(@"(?i)(?:\(d(?<device>\d+)\)\s*)?::HF_EVENT_(?<event>[A-Z0-9_]+)", RegexOptions.CultureInvariant, 100)]
    private static partial Regex HfpEventRegex();

    [GeneratedRegex(@"(?i)\(conn\s+call\s+setup\s+held\s+audio\)\s*=\s*\((?<connected>\d+)\s+(?<call>\d+)\s+(?<setup>\d+)\s+(?<held>\d+)\s+(?<audio>\d+)\)", RegexOptions.CultureInvariant, 100)]
    private static partial Regex HfpStateTupleRegex();

    [GeneratedRegex(@"(?i)\brfcomm_report_(?<event>opened|closed|connect_req)\s*:?[ ]*(?<details>.*)$", RegexOptions.CultureInvariant, 100)]
    private static partial Regex RfcommEventRegex();

    [GeneratedRegex(@"(?i)^(?<subsystem>BLE|GATT)\s+(?<code>[0-9A-F]{4})\s+#\d+\s+(?<tag>[A-Z0-9_]+)(?:\s|$)", RegexOptions.CultureInvariant, 100)]
    private static partial Regex BesBleRecordRegex();

    [GeneratedRegex(@"(?i)\bBudEvt:input=(?<input>[A-Z0-9_]+),\s*ori_state=(?<old>[A-Z0-9_]+)", RegexOptions.CultureInvariant, 100)]
    private static partial Regex BoxEventRegex();

    [GeneratedRegex(@"(?i)\bear_putinout\s*=\s*(?<state>[A-Z0-9_]+)", RegexOptions.CultureInvariant, 100)]
    private static partial Regex ChargeStateRegex();

    [GeneratedRegex(@"(?i)\b(?:boot_cause|boot_reason)\s*[:=]\s*(?<reason>0x[0-9A-F]+)", RegexOptions.CultureInvariant, 100)]
    private static partial Regex BootReasonRegex();

    [GeneratedRegex(@"(?i)\bavrcp_key\b", RegexOptions.CultureInvariant, 100)]
    private static partial Regex AvrcpKeyRegex();

    [GeneratedRegex(@"(?i)\bAVRCP\b", RegexOptions.CultureInvariant, 100)]
    private static partial Regex StandardAvrcpRegex();

    [GeneratedRegex(@"(?i)\bpause\b", RegexOptions.CultureInvariant, 100)]
    private static partial Regex AvrcpPauseRegex();

    [GeneratedRegex(@"(?i)\bplay\b", RegexOptions.CultureInvariant, 100)]
    private static partial Regex AvrcpPlayRegex();

    [GeneratedRegex(@"(?i)\bnotify\b|\bnotification\b", RegexOptions.CultureInvariant, 100)]
    private static partial Regex AvrcpNotifyRegex();

    [GeneratedRegex(@"(?i)(?:\bHFP\b|::HF\b|\bAT(?:A|D|\+CHUP|\+BAC|\+BCS))", RegexOptions.CultureInvariant, 100)]
    private static partial Regex HfpMarkerRegex();

    [GeneratedRegex(@"(?i)(?:\bRFCOMM\b|\bDLCI\b)", RegexOptions.CultureInvariant, 100)]
    private static partial Regex StandardRfcommRegex();

    [GeneratedRegex(@"(?i)\bA2DP\b", RegexOptions.CultureInvariant, 100)]
    private static partial Regex StandardA2dpRegex();

    [GeneratedRegex(@"(?i)\bGATT\b", RegexOptions.CultureInvariant, 100)]
    private static partial Regex StandardGattRegex();

    [GeneratedRegex(@"(?i)\bHCI\s+(?:connection|disconnection|command|authentication|encryption)", RegexOptions.CultureInvariant, 100)]
    private static partial Regex HciTextRegex();
}
