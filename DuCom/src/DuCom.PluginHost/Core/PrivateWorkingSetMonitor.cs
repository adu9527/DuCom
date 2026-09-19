using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DuCom.PluginHost.Core;

/// <summary>Resident private bytes only; never substitutes commit or total working set.</summary>
public static class PrivateWorkingSetMonitor
{
    public static ulong? Sample(uint hostPid, IEnumerable<uint> pluginPids)
        => SampleBreakdown(hostPid, pluginPids).TotalBytes;

    public static Snapshot SampleBreakdown(uint hostPid, IEnumerable<uint> pluginPids)
        => SumBreakdown(hostPid, pluginPids, ReadProcess);

    public readonly record struct Snapshot(ulong? HostBytes, ulong? PluginBytes)
    {
        public ulong? TotalBytes => checked(HostBytes + PluginBytes);
    }

    internal static ulong? Sum(uint hostPid, IEnumerable<uint> pluginPids, Func<uint, Reading> read)
        => SumBreakdown(hostPid, pluginPids, read).TotalBytes;

    internal static Snapshot SumBreakdown(uint hostPid, IEnumerable<uint> pluginPids, Func<uint, Reading> read)
    {
        ulong? host = null;
        ulong? plugins = 0;
        foreach (uint pid in pluginPids.Prepend(hostPid).Distinct())
        {
            Reading sample;
            try { sample = read(pid); }
            catch (Win32Exception) { sample = new(null); }
            if (sample.Exited && pid != hostPid) continue;
            if (pid == hostPid)
                host = sample.Exited ? null : sample.Bytes;
            else
                // Nullable addition keeps the entire plugin group unavailable after any failure.
                plugins = checked(plugins + sample.Bytes);
        }
        return new(host, plugins);
    }

    internal readonly record struct Reading(ulong? Bytes, bool Exited = false);

    internal static Reading ReadProcess(uint pid)
    {
        // Modern GetProcessMemoryInfo needs QUERY_LIMITED_INFORMATION, not VM_READ.
        // SYNCHRONIZE lets us distinguish an exited process (including exit code 259).
        using SafeProcessHandle process = OpenProcess(0x1000 | 0x00100000, false, pid);
        if (process.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            if (pid != 0 && error == 87) return new(null, Exited: true);
            throw new Win32Exception(error, $"Cannot open memory-monitor process {pid}.");
        }

        uint size = (uint)Marshal.SizeOf<ProcessMemoryCountersEx2>();
        // Older implementations may accept a larger buffer but only fill the EX prefix.
        // A sentinel prevents unsupported EX2 from masquerading as a zero-byte sample.
        ProcessMemoryCountersEx2 counters = new() { Size = size, PrivateWorkingSetSize = nuint.MaxValue };
        bool succeeded = GetProcessMemoryInfo(process, ref counters, size);
        int memoryError = Marshal.GetLastWin32Error();
        uint wait = WaitForSingleObject(process, 0);
        if (wait == 0) return new(null, Exited: true);
        if (wait != 258) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot confirm memory-monitor process liveness.");
        if (!succeeded)
        {
            if (memoryError is 87 or 50 or 120) return new(null);
            throw new Win32Exception(memoryError, $"Cannot sample private working set for process {pid}.");
        }
        return counters.PrivateWorkingSetSize == nuint.MaxValue
            ? new(null)
            : new((ulong)counters.PrivateWorkingSetSize);
    }

    // https://learn.microsoft.com/windows/win32/api/psapi/ns-psapi-process_memory_counters_ex2
    // EX2: Windows 10/11 22H2 + September 2023 update. This API has no flags parameter.
    [StructLayout(LayoutKind.Sequential)]
    internal struct ProcessMemoryCountersEx2
    {
        public uint Size, PageFaultCount;
        public nuint PeakWorkingSetSize, WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage;
        public nuint PagefileUsage, PeakPagefileUsage, PrivateUsage, PrivateWorkingSetSize;
        public ulong SharedCommitUsage;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint pid);

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessMemoryInfo(SafeProcessHandle process, ref ProcessMemoryCountersEx2 counters, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
}
