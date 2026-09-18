using System.Globalization;
using System.Text.RegularExpressions;

namespace DuCom.Core.LogAnalysis;

public static partial class BesFaultDecoder
{
    private const string FatalModule = "系统故障";
    private const string MemoryModule = "内存故障";
    private const string ThreadModule = "线程故障";
    private const string DiagnosticModule = "故障诊断";

    private static readonly Dictionary<string, string> FaultTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MemFault"] = "内存保护异常",
        ["BusFault"] = "总线访问异常",
        ["HardFault"] = "硬异常",
        ["UsageFault"] = "指令或执行状态异常",
        ["SecureFault"] = "TrustZone 安全异常",
        ["NMI"] = "不可屏蔽中断异常",
        ["Monitor"] = "调试监控异常",
        ["None"] = "未识别异常类型",
    };

    private static readonly Dictionary<string, string> FaultCauses = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Instruction access violation"] = "取指访问违反内存权限",
        ["Data access violation"] = "数据访问违反内存权限",
        ["Instruction bus error"] = "取指总线错误，可能跳转到无效或不可执行地址",
        ["Precise data bus error"] = "精确数据总线错误，保存的 PC 通常接近故障指令",
        ["Imprecise data bus error"] = "非精确数据总线错误，保存的 PC 可能晚于实际故障写操作",
        ["Undefined instruction UsageFault"] = "执行了未定义或损坏的指令",
        ["Invalid state UsageFault"] = "处理器执行状态或跳转地址无效",
        ["Invalid PC load by EXC_RETURN UsageFault"] = "异常返回时恢复了无效 PC，异常栈帧可能损坏",
        ["No coprocessor UsageFault"] = "访问了不可用的协处理器",
        ["Stack overflow UsageFault"] = "硬件栈限制检测到栈溢出",
        ["Unaligned access UsageFault"] = "发生不允许的非对齐访问",
        ["Divide by zero UsageFault"] = "执行整数除零并触发 UsageFault",
        ["MMFAR valid"] = "MMFAR 故障地址有效",
        ["BFAR valid"] = "BFAR 故障地址有效",
        ["None"] = "未识别异常原因",
    };

    public static IReadOnlyList<BluetoothHciAnalysis> DecodeAll(string text)
    {
        List<BluetoothHciAnalysis> analyses = [];
        DecodeAssertion(text, analyses);
        DecodeProcessorFault(text, analyses);
        DecodeMemoryFault(text, analyses);
        DecodeThreadFault(text, analyses);
        DecodeCrashDump(text, analyses);
        return analyses;
    }

    public static bool IsFatal(BluetoothHciAnalysis analysis) => analysis.Module is FatalModule or MemoryModule or ThreadModule;

    public static bool IsWarning(BluetoothHciAnalysis analysis) =>
        analysis.Module == DiagnosticModule && analysis.Keyword is "Low Thread Stack" or "Null Pointer Detected";

    private static void DecodeAssertion(string text, List<BluetoothHciAnalysis> analyses)
    {
        Match assertion = AssertRegex().Match(text);
        if (assertion.Success)
            Add(analyses, FatalModule, "Firmware Assert", $"固件主动断言失败，调用地址 0x{assertion.Groups["address"].Value.ToUpperInvariant()}；需结合断言消息和对应版本 ELF 定位");

        AddMatch(analyses, AssertFileRegex(), text, DiagnosticModule, "Assert Source File", match => $"断言源文件：{match.Groups["value"].Value.Trim()}");
        AddMatch(analyses, AssertFunctionRegex(), text, DiagnosticModule, "Assert Function", match => $"断言所在函数：{match.Groups["value"].Value.Trim()}");
        AddMatch(analyses, AssertLineRegex(), text, DiagnosticModule, "Assert Source Line", match => $"断言源代码行号：{match.Groups["value"].Value}");

        Match bleAssert = BleAssertInfoRegex().Match(text);
        if (bleAssert.Success)
            Add(analyses, DiagnosticModule, "BLE Assert Info",
                $"BLE 协议栈条件检查失败：函数 {bleAssert.Groups["function"].Value.Trim()}，行 {bleAssert.Groups["line"].Value}，参数 {bleAssert.Groups["first"].Value}、{bleAssert.Groups["second"].Value}；该宏不一定导致崩溃");
    }

    private static void DecodeProcessorFault(string text, List<BluetoothHciAnalysis> analyses)
    {
        AddFixed(analyses, text, "### EXCEPTION ###", FatalModule, "Processor Exception", "检测到处理器异常，需结合 FaultInfo、FaultCause、PC/LR 和故障地址判断根因");

        Match info = FaultInfoRegex().Match(text);
        if (info.Success) Add(analyses, FatalModule, "Processor Fault Type", "处理器异常类型：" + TranslateList(info.Groups["values"].Value, FaultTypes));

        Match cause = FaultCauseRegex().Match(text);
        if (cause.Success) Add(analyses, FatalModule, "Processor Fault Cause", "处理器异常原因：" + TranslateList(cause.Groups["values"].Value, FaultCauses, "；"));

        AddFixed(analyses, text, "(Escalation HardFault)", FatalModule, "Escalated HardFault", "其他可配置异常未被处理或被禁用，已升级为 HardFault");
        AddFixed(analyses, text, "Possible Backtrace:", DiagnosticModule, "Possible Backtrace", "以下地址是启发式调用栈候选，需使用完全匹配的 ELF 符号化确认");
        AddFixed(analyses, text, "Stack:", DiagnosticModule, "Raw Stack Dump", "以下内容为异常现场原始栈内存转储");

        Match registers = FaultRegistersRegex().Match(text);
        if (registers.Success)
            Add(analyses, DiagnosticModule, "Fault Status Registers", $"异常状态寄存器：SHCSR={registers.Groups["shcsr"].Value}，CFSR={registers.Groups["cfsr"].Value}，HFSR={registers.Groups["hfsr"].Value}，AFSR={registers.Groups["afsr"].Value}");

        Match addresses = FaultAddressRegex().Match(text);
        if (addresses.Success)
            Add(analyses, DiagnosticModule, "Fault Address Registers", $"异常地址寄存器：MMFAR={addresses.Groups["mmfar"].Value}，BFAR={addresses.Groups["bfar"].Value}");
    }

    private static void DecodeMemoryFault(string text, List<BluetoothHciAnalysis> analyses)
    {
        Match realloc = ReallocNoMemoryRegex().Match(text);
        if (realloc.Success)
            Add(analyses, MemoryModule, "Memory Reallocation Failed", $"内存重新分配失败：原指针 {realloc.Groups["pointer"].Value}，请求 {realloc.Groups["size"].Value} 字节");
        else
        {
            Match noMemory = NoMemoryRegex().Match(text);
            if (noMemory.Success)
            {
                string free = noMemory.Groups["free"].Success ? $"，当前空闲 {noMemory.Groups["free"].Value} 字节" : string.Empty;
                Add(analyses, MemoryModule, "Memory Allocation Failed", $"内存分配失败：请求 {noMemory.Groups["size"].Value} 字节{free}");
            }
        }

        Match pool = PoolShortageRegex().Match(text);
        if (pool.Success) Add(analyses, MemoryModule, "System Pool Shortage", $"系统内存池不足：请求 {pool.Groups["size"].Value} 字节，剩余 {pool.Groups["free"].Value} 字节");

        AddFixed(analyses, text, "Cannot malloc any RAM", MemoryModule, "All Heaps Exhausted", "所有已注册内存堆均无法满足分配请求");
        AddFixed(analyses, text, "cannot malloc memory", MemoryModule, "FreeRTOS Malloc Failed", "FreeRTOS 内存分配失败钩子被调用；系统已处于内存不足状态");

        Match corrupt = HeapCorruptionRegex().Match(text);
        if (corrupt.Success)
        {
            string kind = corrupt.Groups["kind"].Value;
            string comment = kind.Equals("Bad head", StringComparison.OrdinalIgnoreCase)
                ? "堆块头部保护值损坏，可能存在缓冲区下溢写或无效指针"
                : kind.Equals("Bad tail", StringComparison.OrdinalIgnoreCase)
                    ? "堆块尾部保护值损坏，通常表示缓冲区越界写"
                    : "堆保护数据损坏，可能存在释放后使用或堆元数据破坏";
            Add(analyses, MemoryModule, "Heap Corruption", $"{comment}；位置 {corrupt.Groups["address"].Value}，期望 {corrupt.Groups["expected"].Value}，实际 {corrupt.Groups["actual"].Value}");
        }

        Match heap = HeapCorruptRegex().Match(text);
        if (heap.Success) Add(analyses, MemoryModule, "Heap Metadata Corruption", $"堆链表或块元数据一致性检查失败，相关地址 {heap.Groups["address"].Value}");

        if (NullPointerRegex().IsMatch(text))
            Add(analyses, DiagnosticModule, "Null Pointer Detected", "代码检测到空指针参数；是否发生真实解引用需结合 FaultCause 和故障地址确认");
    }

    private static void DecodeThreadFault(string text, List<BluetoothHciAnalysis> analyses)
    {
        Match rtx = RtxErrorRegex().Match(text);
        if (rtx.Success)
        {
            uint code = uint.Parse(rtx.Groups["code"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            string reason = code switch
            {
                1 => "RTX 检测到线程栈越界",
                2 => "RTX ISR 后处理队列溢出",
                3 => "RTX 用户定时器回调队列溢出",
                4 => "RTX C/C++ 线程库空间不足",
                5 => "RTX C/C++ 库互斥量初始化失败",
                _ => $"RTX 内核错误码 0x{code:X8}",
            };
            Add(analyses, ThreadModule, "RTX Kernel Error", $"{reason}；线程/对象地址 {rtx.Groups["object"].Value}");
        }

        Match freeRtos = FreeRtosStackRegex().Match(text);
        if (freeRtos.Success) Add(analyses, ThreadModule, "FreeRTOS Stack Overflow", $"FreeRTOS 检测到任务栈溢出：{freeRtos.Groups["task"].Value.Trim()}");

        Match blocked = BlockedThreadRegex().Match(text);
        if (blocked.Success) Add(analyses, ThreadModule, "Thread Hung", $"线程 {blocked.Groups["thread"].Value} 已连续 {blocked.Groups["duration"].Value} ms 未获得运行机会；可能是死锁、任务饥饿、长时间关中断或高优先级死循环");

        AddFixed(analyses, text, "Find thread hung", ThreadModule, "Thread Hung", "RTX 检测到线程长时间未获得运行机会，系统可能存在死锁、饥饿或调度停滞");
        AddFixed(analyses, text, "System soft lockup", ThreadModule, "System Soft Lockup", "系统软锁死：空闲任务健康检查失败，可能由死循环、长时间关中断、任务饥饿或死锁引起");

        Match stack = ThreadStackRegex().Match(text);
        if (stack.Success)
        {
            int size = int.Parse(stack.Groups["size"].Value, CultureInfo.InvariantCulture);
            int free = int.Parse(stack.Groups["free"].Value, CultureInfo.InvariantCulture);
            bool low = free * 10 <= size;
            Add(analyses, DiagnosticModule, low ? "Low Thread Stack" : "Thread Stack Usage",
                $"线程栈：总大小 {size} 字节，历史最小剩余 {free} 字节{(low ? "，剩余空间低于或等于 10%，存在栈溢出风险" : string.Empty)}");
        }
    }

    private static void DecodeCrashDump(string text, List<BluetoothHciAnalysis> analyses)
    {
        AddFixed(analyses, text, "CRASH ENCOUNTERED", FatalModule, "CrashCatcher Started", "CrashCatcher 已开始输出崩溃核心转储");
        AddFixed(analyses, text, "End of crash dump", DiagnosticModule, "CrashCatcher Completed", "CrashCatcher 崩溃核心转储输出结束");
        AddFixed(analyses, text, "coredump_to_flash_init failed", DiagnosticModule, "Coredump Storage Init Failed", "核心转储 Flash 区域初始化失败，后续崩溃现场可能无法持久化保存");

        Match dump = CrashDumpRegex().Match(text);
        if (dump.Success) Add(analyses, DiagnosticModule, "Crash Dump Storage", $"崩溃日志 Flash 记录：序号 {dump.Groups["sequence"].Value}，偏移 {dump.Groups["offset"].Value}；该信息不是崩溃根因");
    }

    private static string TranslateList(string text, IReadOnlyDictionary<string, string> translations, string separator = "、") =>
        string.Join(separator, ParenthesizedRegex().Matches(text).Select(match => translations.GetValueOrDefault(match.Groups["value"].Value, match.Groups["value"].Value)));

    private static void AddMatch(List<BluetoothHciAnalysis> analyses, Regex regex, string text, string module, string keyword, Func<Match, string> comment)
    {
        Match match = regex.Match(text);
        if (match.Success) Add(analyses, module, keyword, comment(match));
    }

    private static void AddFixed(List<BluetoothHciAnalysis> analyses, string text, string pattern, string module, string keyword, string comment)
    {
        if (text.Contains(pattern, StringComparison.OrdinalIgnoreCase)) Add(analyses, module, keyword, comment);
    }

    private static void Add(List<BluetoothHciAnalysis> analyses, string module, string keyword, string comment)
    {
        BluetoothHciAnalysis analysis = new(module, keyword, comment);
        if (!analyses.Contains(analysis)) analyses.Add(analysis);
    }

    [GeneratedRegex(@"###\s*ASSERT\s*@\s*0x(?<address>[0-9A-F]+)\s*###", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)] private static partial Regex AssertRegex();
    [GeneratedRegex(@"^FILE\s*:\s*(?<value>.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)] private static partial Regex AssertFileRegex();
    [GeneratedRegex(@"^FUNCTION\s*:\s*(?<value>.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)] private static partial Regex AssertFunctionRegex();
    [GeneratedRegex(@"^LINE\s*:\s*(?<value>\d+)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)] private static partial Regex AssertLineRegex();
    [GeneratedRegex(@"line is\s*(?<line>\d+)\s*,\s*function\s*(?<function>[^,]+)\s*,\s*(?<first>0x[0-9A-F]+)\s*,\s*(?<second>0x[0-9A-F]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)] private static partial Regex BleAssertInfoRegex();
    [GeneratedRegex(@"^FaultInfo\s*:\s*(?<values>.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)] private static partial Regex FaultInfoRegex();
    [GeneratedRegex(@"^FaultCause\s*:\s*(?<values>.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)] private static partial Regex FaultCauseRegex();
    [GeneratedRegex(@"\((?<value>[^)]+)\)", RegexOptions.CultureInvariant, 100)] private static partial Regex ParenthesizedRegex();
    [GeneratedRegex(@"SHCSR=(?<shcsr>[0-9A-F]+),\s*CFSR\s*=(?<cfsr>[0-9A-F]+),\s*HFSR\s*=(?<hfsr>[0-9A-F]+),\s*AFSR\s*=(?<afsr>[0-9A-F]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)] private static partial Regex FaultRegistersRegex();
    [GeneratedRegex(@"MMFAR=(?<mmfar>[0-9A-F]+),\s*BFAR\s*=(?<bfar>[0-9A-F]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)] private static partial Regex FaultAddressRegex();
    [GeneratedRegex(@"\[[^\]]+\]\s+no memory:\s*size=(?<size>\d+)(?:,\s*free:(?<free>\d+))?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)] private static partial Regex NoMemoryRegex();
    [GeneratedRegex(@"\[[^\]]+\]\s+no memory:\s*ptr=(?<pointer>\S+)\s+size=(?<size>\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)] private static partial Regex ReallocNoMemoryRegex();
    [GeneratedRegex(@"System pool in shortage!\s*To allocate size\s*(?<size>\d+)\s*but free size\s*(?<free>\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)] private static partial Regex PoolShortageRegex();
    [GeneratedRegex(@"CORRUPT HEAP:\s*(?<kind>Bad head|Bad tail|Invalid data).*?at\s*(?<address>\S+)\.\s*Expected\s*(?<expected>\S+)\s+got\s*(?<actual>\S+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)] private static partial Regex HeapCorruptionRegex();
    [GeneratedRegex(@"Heap corrupt:\s*(?<address>\S+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)] private static partial Regex HeapCorruptRegex();
    [GeneratedRegex(@"(?:null pointer|null ptr)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)] private static partial Regex NullPointerRegex();
    [GeneratedRegex(@"osRtxErrorNotify,\s*code:\s*(?<code>[0-9A-F]+)\s*object is\s*(?<object>\S+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)] private static partial Regex RtxErrorRegex();
    [GeneratedRegex(@"task\s+(?<task>.+?)\s+stack overflow\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)] private static partial Regex FreeRtosStackRegex();
    [GeneratedRegex("Thread\\s+\\\"(?<thread>[^\\\"]+)\\\"\\s+blocked for\\s+(?<duration>\\d+)ms", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)] private static partial Regex BlockedThreadRegex();
    [GeneratedRegex(@"stack_mem=0x[0-9A-F]+\s+stack_size=(?<size>\d+)\s+sp:0x[0-9A-F]+\s+min_stack_free=(?<free>\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)] private static partial Regex ThreadStackRegex();
    [GeneratedRegex(@"__CRASH_DUMP:.*dump_seqnum\s*=\s*(?<sequence>0x[0-9A-F]+),\s*flash_offset\s*=\s*(?<offset>0x[0-9A-F]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)] private static partial Regex CrashDumpRegex();
}
