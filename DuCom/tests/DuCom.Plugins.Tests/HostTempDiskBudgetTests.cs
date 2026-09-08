using DuCom.PluginHost.Core;
using System.Diagnostics;
using Xunit;

namespace DuCom.Plugins.Tests;

public sealed class HostTempDiskBudgetTests
{
    [Fact]
    public async Task ConcurrentReservationsNeverExceedSharedLimit()
    {
        HostTempDiskBudget budget = new(100);
        bool[] results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => budget.TryReserve(10))));
        Assert.Equal(10, results.Count(result => result));
        Assert.Equal(100, budget.ReservedBytes);
        foreach (bool result in results) if (result) budget.Release(10);
        Assert.Equal(0, budget.ReservedBytes);
    }

    [Fact]
    public void LedgerCleansOnlyAbandonedRegisteredResources()
    {
        string root = Path.Combine(Path.GetTempPath(), $"ducom-ledger-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(root);
            HostTempDiskBudget budget = new(1024);
            string activeFile = Path.Combine(root, "HostOutput", "active.part");
            string abandonedFile = Path.Combine(root, "HostOutput", "abandoned.part");
            Directory.CreateDirectory(Path.GetDirectoryName(activeFile)!);
            File.WriteAllText(activeFile, "active");
            File.WriteAllText(abandonedFile, "abandoned");
            using HostTempResourceLedger active = new(root, "active", budget);
            active.RegisterIntent(Record("active-resource", "active", activeFile));
            using (HostTempResourceLedger abandoned = new(root, "abandoned", budget))
                abandoned.RegisterIntent(Record("abandoned-resource", "abandoned", abandonedFile));

            using HostTempResourceLedger recovery = new(root, "recovery", budget);
            Assert.True(File.Exists(activeFile));
            Assert.False(File.Exists(abandonedFile));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task KilledHostProcessResourcesAreRecoveredWithoutTouchingLiveHost()
    {
        string root = Path.Combine(Path.GetTempPath(), $"ducom-ledger-process-{Guid.NewGuid():N}");
        string ready = Path.Combine(root, "ready.txt");
        Process? worker = null;
        try
        {
            Directory.CreateDirectory(root);
            string workerDll = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "DuCom.HostRecoveryWorker", "bin", "Debug", "net10.0", "DuCom.HostRecoveryWorker.dll"));
            worker = Process.Start(new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                ArgumentList = { workerDll, root, "killed", ready },
            })!;
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
            while (!File.Exists(ready)) await Task.Delay(25, timeout.Token);
            string killedResource = File.ReadAllText(ready);

            HostTempDiskBudget budget = new(1024 * 1024);
            string livePath = Path.Combine(root, "HostOutput", "live", "active.part");
            Directory.CreateDirectory(Path.GetDirectoryName(livePath)!);
            File.WriteAllText(livePath, "live");
            using HostTempResourceLedger live = new(root, "live", budget);
            live.RegisterIntent(Record("live-resource", "live", livePath));

            worker.Kill(entireProcessTree: true);
            await worker.WaitForExitAsync(timeout.Token);
            using HostTempResourceLedger recovery = new(root, "recovery", budget);
            Assert.False(File.Exists(killedResource));
            Assert.True(File.Exists(livePath));
        }
        finally
        {
            if (worker is { HasExited: false }) worker.Kill(entireProcessTree: true);
            worker?.Dispose();
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static HostTempResourceRecord Record(string resourceId, string activationId, string path) => new()
    {
        ResourceId = resourceId,
        PluginId = "org.example.ledger",
        ActivationId = activationId,
        Kind = HostTempResourceKind.OutputFile,
        Path = path,
        PluginLimitBytes = 1024,
    };
}
