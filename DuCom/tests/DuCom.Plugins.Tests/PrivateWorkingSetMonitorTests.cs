using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using DuCom.PluginHost.Core;
using Xunit;

namespace DuCom.Plugins.Tests;

public sealed class PrivateWorkingSetMonitorTests
{
    [Fact]
    public void SumsHostWorkersAndHelpersOnceWithoutUnrelatedProcesses()
    {
        List<uint> sampled = [];
        var sample = PrivateWorkingSetMonitor.SumBreakdown(1, [2, 3, 2, 1], pid =>
        {
            sampled.Add(pid);
            return new(pid * 10UL);
        });
        Assert.Equal(10UL, sample.HostBytes);
        Assert.Equal(50UL, sample.PluginBytes);
        Assert.Equal(60UL, sample.TotalBytes);
        Assert.Equal(sample.HostBytes + sample.PluginBytes, sample.TotalBytes);
        Assert.Equal(new uint[] { 1, 2, 3 }, sampled);
    }

    [Fact]
    public void ConfirmedWorkerExitIsSkippedButHostExitIsUnavailable()
    {
        Assert.Equal(10UL, PrivateWorkingSetMonitor.Sum(1, [2], pid => pid == 1 ? new(10) : new(null, true)));
        Assert.Null(PrivateWorkingSetMonitor.Sum(1, [2], _ => new(null, true)));
    }

    [Fact]
    public void UnsupportedAndAccessDeniedNeverReturnPartialTotal()
    {
        Assert.Null(PrivateWorkingSetMonitor.Sum(1, [2], pid => pid == 1 ? new(10) : new(null)));
        Assert.Null(PrivateWorkingSetMonitor.Sum(1, [2], pid =>
            pid == 1 ? new(10) : throw new Win32Exception(5)));
        Assert.Throws<OverflowException>(() => PrivateWorkingSetMonitor.Sum(1, [2], _ => new(ulong.MaxValue)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedPluginInvalidatesWholePluginGroupButPreservesHost(bool accessDenied)
    {
        List<uint> sampled = [];
        var sample = PrivateWorkingSetMonitor.SumBreakdown(1, [2, 3, 4], pid =>
        {
            sampled.Add(pid);
            return pid == 3 ? accessDenied ? throw new Win32Exception(5) : new(null) : new(pid * 10UL);
        });
        Assert.Equal(10UL, sample.HostBytes);
        Assert.Null(sample.PluginBytes);
        Assert.Null(sample.TotalBytes);
        Assert.Equal(new uint[] { 1, 2, 3, 4 }, sampled);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void FailedHostPreservesCompletePluginGroup(bool exited, bool accessDenied)
    {
        var sample = PrivateWorkingSetMonitor.SumBreakdown(1, [2, 3], pid =>
            pid == 1 ? accessDenied ? throw new Win32Exception(5) : new(null, exited) : new(pid * 10UL));
        Assert.Null(sample.HostBytes);
        Assert.Equal(50UL, sample.PluginBytes);
        Assert.Null(sample.TotalBytes);
    }

    [Fact]
    public void NoPluginsOrOnlyExitedPluginsHaveZeroPluginBytes()
    {
        var empty = PrivateWorkingSetMonitor.SumBreakdown(1, [], _ => new(10));
        var exited = PrivateWorkingSetMonitor.SumBreakdown(1, [2, 3], pid => pid == 1 ? new(10) : new(null, true));
        Assert.Equal(new PrivateWorkingSetMonitor.Snapshot(10, 0), empty);
        Assert.Equal(empty, exited);
        Assert.Equal(10UL, exited.TotalBytes);
    }

    [Fact]
    public void RegistrationSnapshotIncludesHelpersWithoutChangingBudgetWorkers()
    {
        using BudgetGovernor budget = new(new());
        budget.RegisterWorker("test", 10);
        budget.RegisterMemoryMonitorHelper(20);
        uint[] snapshot = budget.GetMemoryMonitorProcessIds();
        Assert.Equal(new uint[] { 10, 20 }, snapshot.Order());
        budget.UnregisterWorker(10);
        budget.UnregisterMemoryMonitorHelper(20);
        Assert.Empty(budget.GetMemoryMonitorProcessIds());
        Assert.Equal(2, snapshot.Length);
    }

    [Fact]
    public void Ex2LayoutMatchesWindowsAbi()
    {
        Assert.Equal(IntPtr.Size == 8 ? 96 : 56, Marshal.SizeOf<PrivateWorkingSetMonitor.ProcessMemoryCountersEx2>());
        Assert.Equal(IntPtr.Size == 8 ? 80 : 44,
            Marshal.OffsetOf<PrivateWorkingSetMonitor.ProcessMemoryCountersEx2>("PrivateWorkingSetSize").ToInt32());
    }

    [Fact]
    public void WindowsInteropReadsResidentPrivatePagesNotCommit()
    {
        // CI on the supported Windows image must exercise EX2, not silently pass an unavailable sample.
        var before = PrivateWorkingSetMonitor.ReadProcess((uint)Environment.ProcessId);
        Assert.False(before.Exited);
        Assert.True(before.Bytes > 0, "EX2 private working set unavailable on this Windows installation.");
        const int size = 16 * 1024 * 1024;
        IntPtr memory = Marshal.AllocHGlobal(size);
        try
        {
            for (int offset = 0; offset < size; offset += Environment.SystemPageSize)
                Marshal.WriteByte(memory, offset, 1);
            var after = PrivateWorkingSetMonitor.ReadProcess((uint)Environment.ProcessId);
            Assert.True(after.Bytes >= before.Bytes + size / 2UL);
        }
        finally { Marshal.FreeHGlobal(memory); }
    }

    [Fact]
    public void WindowsInteropConfirmsExitedPidAndRejectsInvalidAccessTarget()
    {
        using Process child = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 259") { UseShellExecute = false, CreateNoWindow = true })!;
        uint pid = (uint)child.Id;
        Assert.True(child.WaitForExit(10000));
        Assert.True(PrivateWorkingSetMonitor.ReadProcess(pid).Exited);
        Assert.Throws<Win32Exception>(() => PrivateWorkingSetMonitor.ReadProcess(0));
    }
}
