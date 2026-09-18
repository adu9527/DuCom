using System.Globalization;
using System.Text.RegularExpressions;

namespace DuCom.Core.LogAnalysis;

public static partial class BesRuntimeDecoder
{
    public static IReadOnlyList<BluetoothHciAnalysis> DecodeAll(string text)
    {
        List<BluetoothHciAnalysis> analyses = [];
        AddMatch(analyses, KernelRegex(), text, "系统运行", "RTOS Kernel",
            match => $"实时操作系统内核：{match.Groups["kernel"].Value.ToUpperInvariant()}");
        AddMatch(analyses, CpuFrequencyRegex(), text, "系统频率", "CPU Frequency",
            match => $"当前 CPU 主频：{match.Groups["value"].Value} MHz");
        AddMatch(analyses, CalculatedFrequencyRegex(), text, "系统频率", "Calculated System Frequency",
            match => $"系统频率计算结果：{FormatHertz(match.Groups["value"].Value)}");
        AddMatch(analyses, A2dpSysfreqRegex(), text, "系统频率", "A2DP Sysfreq Request",
            match => $"A2DP 播放请求系统频率枚举：{match.Groups["value"].Value}（芯片相关原始值）");
        AddMatch(analyses, SysfreqUserRegex(), text, "系统频率", "Sysfreq User Request",
            match => $"系统频率请求者 {match.Groups["user"].Value}：频率枚举 {match.Groups["freq"].Value}（芯片相关原始值）");
        AddMatch(analyses, SysfreqTopUserRegex(), text, "系统频率", "Sysfreq Top User",
            match => $"当前最高系统频率请求者：{match.Groups["user"].Value}（原始请求者 ID）");
        AddFixed(analyses, text, "SYSFREQ USER FREQ:", "系统频率", "Sysfreq Request Snapshot", "系统频率请求者快照开始");

        Match cpuUsage = CpuUsageRegex().Match(text);
        if (cpuUsage.Success)
        {
            bool cp = cpuUsage.Groups["cp"].Success;
            List<string> fields = UsageFieldRegex().Matches(cpuUsage.Groups["fields"].Value)
                .Select(match => $"{UsageFieldName(match.Groups["name"].Value)} {match.Groups["value"].Value}%")
                .ToList();
            analyses.Add(new BluetoothHciAnalysis("系统负载", cp ? "CP CPU Usage" : "CPU Usage",
                $"{(cp ? "协处理器" : "CPU")}利用率：{string.Join("，", fields)}"));
        }

        AddMatch(analyses, BtStackSizeRegex(), text, "线程与内存", "Bluetooth Stack Size",
            match => $"蓝牙协议栈线程栈大小：{match.Groups["bytes"].Value} 字节（0x{match.Groups["hex"].Value.ToUpperInvariant()}）");
        AddMatch(analyses, HeapSummaryRegex(), text, "线程与内存", "Heap Summary", match =>
            $"系统堆：总计 {FormatBytes(match.Groups["total"].Value)}，当前空闲 {FormatBytes(match.Groups["free"].Value)}，历史最小空闲 {FormatBytes(match.Groups["minimum"].Value)}");

        DecodeThreadLifecycle(text, analyses);
        DecodeSubsystemLifecycle(text, analyses);
        return analyses;
    }

    private static void DecodeThreadLifecycle(string text, List<BluetoothHciAnalysis> analyses)
    {
        Match aux = AuxWorkerRegex().Match(text);
        if (aux.Success)
            analyses.Add(new BluetoothHciAnalysis("线程管理", "AUX Audio Worker Created",
                $"辅助音频工作线程已创建，轮询周期 {aux.Groups["poll"].Value} ms"));

        AddFixed(analyses, text, "enter prompt handler thread", "线程管理", "Prompt Thread Started", "提示音处理线程已进入运行");
        AddFixed(analyses, text, "WPA task started", "线程管理", "WPA Task Started", "WPA 网络安全任务已启动");
        AddFixed(analyses, text, "key_handler_thread started", "线程管理", "Key Handler Thread Started", "按键处理线程已启动");
        AddFixed(analyses, text, "key_handler_thread initialized successfully", "线程管理", "Key Handler Thread Initialized", "按键处理线程初始化成功");
        AddFixed(analyses, text, "rotary_encoder_thread_init: thread created successfully", "线程管理", "Rotary Thread Created", "旋钮编码器线程创建成功");
        AddFixed(analyses, text, "rotary_encoder_thread: thread started", "线程管理", "Rotary Thread Started", "旋钮编码器线程已启动");
        AddFixed(analyses, text, "capsensor_thread start", "线程管理", "Capsensor Thread Started", "电容触摸传感器线程已启动");

        Match sensor = SensorThreadRegex().Match(text);
        if (sensor.Success)
            analyses.Add(new BluetoothHciAnalysis("线程管理", "Sensor Thread Init",
                sensor.Groups["result"].Value == "0" ? "传感器线程初始化成功" : $"传感器线程初始化返回 {sensor.Groups["result"].Value}"));

        Match idle = ServiceThreadIdleRegex().Match(text);
        if (idle.Success)
            analyses.Add(new BluetoothHciAnalysis("线程管理", "MCPP Service Thread State",
                $"MCPP 服务线程{(idle.Groups["state"].Value.Equals("enter", StringComparison.OrdinalIgnoreCase) ? "进入" : "退出")}空闲模式"));
    }

    private static void DecodeSubsystemLifecycle(string text, List<BluetoothHciAnalysis> analyses)
    {
        AddFixed(analyses, text, "bth_rom_init ok", "系统运行", "BTH ROM Initialized", "蓝牙子系统 ROM 初始化成功");
        AddFixed(analyses, text, "btm_chip_init : stack_ready", "系统运行", "Bluetooth Stack Ready", "蓝牙控制器与协议栈已就绪");

        Match stack = BtStackDoneRegex().Match(text);
        if (stack.Success)
            analyses.Add(new BluetoothHciAnalysis("系统运行", "Bluetooth Stack Init Done",
                $"蓝牙协议栈初始化完成，原始状态值 {stack.Groups["value"].Value}（枚举未确认）"));

        Match cp = CpAccelRegex().Match(text);
        if (cp.Success)
            analyses.Add(new BluetoothHciAnalysis("协处理器", cp.Groups["action"].Value.Equals("open", StringComparison.OrdinalIgnoreCase) ? "CP Task Open" : "CP Task Close",
                $"协处理器加速任务{(cp.Groups["action"].Value.Equals("open", StringComparison.OrdinalIgnoreCase) ? "开启" : "关闭")}：任务 ID {cp.Groups["task"].Value}，CP 状态 {cp.Groups["state"].Value}，初始化标志 {cp.Groups["init"].Value}"));

        Match m55 = M55Regex().Match(text);
        if (m55.Success)
            analyses.Add(new BluetoothHciAnalysis("协处理器", m55.Groups["action"].Value.Equals("init", StringComparison.OrdinalIgnoreCase) ? "M55 Subsystem Init" : "M55 Subsystem Deinit",
                $"M55 子系统{(m55.Groups["action"].Value.Equals("init", StringComparison.OrdinalIgnoreCase) ? "初始化" : "释放")}：用户 {m55.Groups["user"].Value}，位图 {m55.Groups["bitmap"].Value}"));

        AddFixed(analyses, text, "feed watchdog", "系统运行", "Watchdog Feed", "系统正常喂狗");
    }

    private static string FormatHertz(string value)
    {
        long hertz = long.Parse(value, CultureInfo.InvariantCulture);
        return hertz % 1_000_000 == 0 ? $"{hertz / 1_000_000} MHz（{hertz} Hz）" : $"{hertz:N0} Hz";
    }

    private static string FormatBytes(string value)
    {
        long bytes = long.Parse(value, CultureInfo.InvariantCulture);
        return bytes >= 1024 ? $"{bytes:N0} 字节（{bytes / 1024d:N1} KiB）" : $"{bytes} 字节";
    }

    private static string UsageFieldName(string name) => name.ToLowerInvariant() switch
    {
        "busy" => "忙碌",
        "light" => "轻度休眠",
        "cpu_sleep" => "CPU 休眠",
        "bus_sleep" => "总线休眠",
        "subsys_sleep" => "子系统休眠",
        "sys_deep" => "系统深度休眠",
        "chip_deep" => "芯片深度休眠",
        "sleep" => "休眠",
        _ => name,
    };

    private static void AddMatch(List<BluetoothHciAnalysis> analyses, Regex regex, string text, string module, string keyword, Func<Match, string> comment)
    {
        Match match = regex.Match(text);
        if (match.Success) analyses.Add(new BluetoothHciAnalysis(module, keyword, comment(match)));
    }

    private static void AddFixed(List<BluetoothHciAnalysis> analyses, string text, string pattern, string module, string keyword, string comment)
    {
        if (text.Contains(pattern, StringComparison.OrdinalIgnoreCase)) analyses.Add(new BluetoothHciAnalysis(module, keyword, comment));
    }

    [GeneratedRegex(@"^KERNEL=(?<kernel>[A-Z0-9_-]+)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)] private static partial Regex KernelRegex();
    [GeneratedRegex(@"\[UIVER\]Cpu Freq\s*=\s*(?<value>\d+)M\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)] private static partial Regex CpuFrequencyRegex();
    [GeneratedRegex(@"\bsysfreq calc\s*:\s*(?<value>\d+)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)] private static partial Regex CalculatedFrequencyRegex();
    [GeneratedRegex(@"\[A2DP_PLAYER\]\s+sysfreq\s+(?<value>\d+)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)] private static partial Regex A2dpSysfreqRegex();
    [GeneratedRegex(@"^\s*\[(?<user>\d+)\]\s+f=(?<freq>\d+)\s*$", RegexOptions.CultureInvariant, 100)] private static partial Regex SysfreqUserRegex();
    [GeneratedRegex(@"^\s*top_user=\s*(?<user>\d+)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)] private static partial Regex SysfreqTopUserRegex();
    [GeneratedRegex(@"^(?<cp>CP\s+)?CPU USAGE:\s*(?<fields>.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)] private static partial Regex CpuUsageRegex();
    [GeneratedRegex(@"(?<name>[A-Za-z_]+)=(?<value>\d+)", RegexOptions.CultureInvariant, 100)] private static partial Regex UsageFieldRegex();
    [GeneratedRegex(@"\bBESBT_STACK_SIZE\s+(?<hex>[0-9A-F]+)\s+(?<bytes>\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)] private static partial Regex BtStackSizeRegex();
    [GeneratedRegex(@"\bTotalHeapSize:(?<total>\d+),\s*FreeHeapSize:(?<free>\d+),\s*MinimumEver:(?<minimum>\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)] private static partial Regex HeapSummaryRegex();
    [GeneratedRegex(@"\[AUX_AUDIO\]\s+worker thread created\s+id=\S+\s+poll=(?<poll>\d+)ms", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)] private static partial Regex AuxWorkerRegex();
    [GeneratedRegex(@"\bsensor_thread_init\s+ret=(?<result>-?\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)] private static partial Regex SensorThreadRegex();
    [GeneratedRegex(@"\bmcpp_srv(?:_low)?_thread,\s*(?<state>enter|exit) the idle mode", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)] private static partial Regex ServiceThreadIdleRegex();
    [GeneratedRegex(@"\bbt_stack_init_done:(?<value>\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)] private static partial Regex BtStackDoneRegex();
    [GeneratedRegex(@"\bcp_accel_(?<action>open|close),\s*task id\s*=\s*(?<task>\d+),\s*cp_state\s*=\s*(?<state>\d+)\s+init\s+(?<init>\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)] private static partial Regex CpAccelRegex();
    [GeneratedRegex(@"\[app_dsp_m55_(?<action>init|deinit)\]\s+user:(?<user>\d+)\s+bitmap:(?<bitmap>0x[0-9A-F]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)] private static partial Regex M55Regex();
}
