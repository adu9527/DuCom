using System.Text.Json;
using DuCom.PluginHost.Core;
using DuCom.PluginHost.Registry;
using Xunit;

namespace DuCom.Plugins.Tests;

[CollectionDefinition("WorkerLifecycle")]
public sealed class WorkerLifecycleCollection
{
}

[Collection("WorkerLifecycle")]
public sealed class WorkerLifecycleTests
{
    [HostBuildFact]
    public async Task OkPluginActivatesWritesStorageAndStopsCleanly()
    {
        using BedHarness harness = BedHarness.Create("ok");
        harness.InstallBed("ok");
        Assert.True(await harness.Service.StartRegisteredAsync("org.example.bed-ok"));
        Assert.Equal(PluginRuntimeState.Active, harness.Service.BuildManagerRows().Single(row => row.Id == "org.example.bed-ok").State);
        Assert.Contains(harness.Environment.Publications, publication => publication.PluginId == "org.example.bed-ok");
        await harness.Service.StopAllAsync();
        string storage = harness.ReadPluginStorage("org.example.bed-ok");
        Assert.Contains("marker", storage);
    }

    [HostBuildFact]
    public async Task CrashDuringInitializationFaultDisablesExactlyOnce()
    {
        using BedHarness harness = BedHarness.Create("crash");
        harness.InstallBed("crash");
        Assert.False(await harness.Service.StartRegisteredAsync("org.example.bed-crash"));

        await WaitForAsync(() => harness.Service.BuildManagerRows().Any(row => row.Id == "org.example.bed-crash" && row.State == PluginRuntimeState.FaultDisabled), TimeSpan.FromSeconds(20));
        Assert.Single(harness.Environment.FaultNotices, notice => notice.PluginId == "org.example.bed-crash");

        PluginRegistryEntry entry = harness.Service.Registry.Current.Plugins["org.example.bed-crash"];
        Assert.NotNull(entry.FaultDisabled);
        Assert.Equal("1.0.0", entry.FaultDisabled!.Version);

        Assert.False(await harness.Service.StartRegisteredAsync("org.example.bed-crash"));
        Assert.Single(harness.Environment.FaultNotices, notice => notice.PluginId == "org.example.bed-crash");
    }

    [HostBuildFact]
    public async Task ConstructorCrashFaultDisables()
    {
        using BedHarness harness = BedHarness.Create("ctor-crash");
        harness.InstallBed("ctor-crash");
        Assert.False(await harness.Service.StartRegisteredAsync("org.example.bed-ctor-crash"));
        await WaitForAsync(() => harness.Service.BuildManagerRows().Any(row => row.Id == "org.example.bed-ctor-crash" && row.State == PluginRuntimeState.FaultDisabled), TimeSpan.FromSeconds(20));
        Assert.Single(harness.Environment.FaultNotices, notice => notice.PluginId == "org.example.bed-ctor-crash");
    }

    [HostBuildFact]
    public async Task ExplicitRetryProducesOneNewNoticePerFailedGeneration()
    {
        using BedHarness harness = BedHarness.Create("retry");
        harness.InstallBed("crash");
        await harness.Service.StartRegisteredAsync("org.example.bed-crash");
        await WaitForAsync(() => harness.Environment.FaultNotices.Any(notice => notice.PluginId == "org.example.bed-crash"), TimeSpan.FromSeconds(20));

        harness.Service.ClearFaultDisable("org.example.bed-crash");
        await harness.Service.StartRegisteredAsync("org.example.bed-crash");
        await WaitForAsync(() => harness.Environment.FaultNotices.Count(notice => notice.PluginId == "org.example.bed-crash") >= 2, TimeSpan.FromSeconds(20));
        Assert.Equal(2, harness.Environment.FaultNotices.Count(notice => notice.PluginId == "org.example.bed-crash"));
    }

    [HostBuildFact]
    public async Task HangDuringActivationTimesOutAndFaultDisables()
    {
        using BedHarness harness = BedHarness.Create("hang");
        harness.InstallBed("hang");
        Assert.False(await harness.Service.StartRegisteredAsync("org.example.bed-hang"));
        await WaitForAsync(() => harness.Service.BuildManagerRows().Any(row => row.Id == "org.example.bed-hang" && row.State == PluginRuntimeState.FaultDisabled), TimeSpan.FromSeconds(25));
        Assert.Contains(harness.Environment.FaultNotices, notice => notice.PluginId == "org.example.bed-hang");
    }

    [HostBuildFact]
    public async Task MessageFloodClosesConnectionAndFaultDisables()
    {
        using BedHarness harness = BedHarness.Create("flood");
        harness.InstallBed("flood");
        // Flood protection may close the connection before activation completes.
        await harness.Service.StartRegisteredAsync("org.example.bed-flood");
        await WaitForAsync(() => harness.Service.BuildManagerRows().Any(row => row.Id == "org.example.bed-flood" && row.State == PluginRuntimeState.FaultDisabled), TimeSpan.FromSeconds(60));
        PluginRegistryEntry entry = harness.Service.Registry.Current.Plugins["org.example.bed-flood"];
        Assert.NotNull(entry.FaultDisabled);
        Assert.Single(harness.Environment.FaultNotices, notice => notice.PluginId == "org.example.bed-flood");
    }

    [HostBuildFact]
    public async Task UnauthorizedPluginIsDeniedByOsAndBroker()
    {
        string secret = Path.Combine(Path.GetTempPath(), $"ducom-bed-secret-{Guid.NewGuid():N}.txt");
        File.WriteAllText(secret, "TOPSECRET");
        Environment.SetEnvironmentVariable("DUCOM_BED_SECRET", secret, EnvironmentVariableTarget.Process);
        try
        {
            using BedHarness harness = BedHarness.Create("unauthorized");
            harness.InstallBed("unauthorized");
            Assert.True(await harness.Service.StartRegisteredAsync("org.example.bed-unauthorized"));

            await WaitForAsync(() =>
            {
                string storage = harness.ReadPluginStorage("org.example.bed-unauthorized");
                return storage.Contains("tcp_connect");
            }, TimeSpan.FromSeconds(15));

            string storageContent = harness.ReadPluginStorage("org.example.bed-unauthorized");
            using JsonDocument document = JsonDocument.Parse(storageContent);
            JsonElement root = document.RootElement;
            Assert.True(
                root.TryGetProperty("read_host_file", out JsonElement readFile) && readFile.GetString() != "ESCAPED",
                $"sandbox probes: {storageContent}");
            Assert.True(root.TryGetProperty("write_temp", out JsonElement writeFile) && writeFile.GetString() != "ESCAPED", $"write probe: {storageContent}");
            Assert.NotEqual("ESCAPED", root.GetProperty("serial_open").GetString());
            Assert.NotEqual("CONNECTED", root.GetProperty("tcp_connect").GetString());
            Assert.True(root.GetProperty("storage_read").GetString() is "null" or "granted", $"storage probe must be granted, got: {storageContent}");
        }
        finally
        {
            Environment.SetEnvironmentVariable("DUCOM_BED_SECRET", null, EnvironmentVariableTarget.Process);
            try
            {
                File.Delete(secret);
            }
            catch (Exception)
            {
            }
        }
    }

    [HostBuildFact]
    public async Task HostOnlyOutputAndSnapshotDirectoriesAreDeniedByOsWhileBrokerOutputWorks()
    {
        using BedHarness harness = BedHarness.Create("host-only-probe");
        harness.InstallBed("host-only-probe");
        Assert.True(await harness.Service.StartRegisteredAsync("org.example.bed-host-only-probe"));
        PluginRuntimeController controller = Assert.IsType<PluginRuntimeController>(harness.Service.GetOrCreateController("org.example.bed-host-only-probe"));
        string outputDirectory = Path.Combine(harness.Paths.TempRoot, "HostOutput", controller.Manifest.Id, controller.ActivationId);
        string snapshotDirectory = Path.Combine(harness.Paths.TempRoot, "HostSnapshots", controller.Manifest.Id, controller.ActivationId);
        string outputFile = Path.Combine(outputDirectory, "known-output.bin");
        string snapshotFile = Path.Combine(snapshotDirectory, "known-snapshot.log");
        File.WriteAllText(outputFile, "output-original");
        File.WriteAllText(snapshotFile, "snapshot-original");

        Assert.True(await controller.InvokeCommandAsync("probe", new Dictionary<string, string>
        {
            ["outputDirectory"] = outputDirectory,
            ["outputFile"] = outputFile,
            ["snapshotDirectory"] = snapshotDirectory,
            ["snapshotFile"] = snapshotFile,
        }));
        await WaitForAsync(() => harness.ReadPluginStorage(controller.Manifest.Id).Contains("broker_output", StringComparison.Ordinal), TimeSpan.FromSeconds(15));
        string stored = harness.ReadPluginStorage(controller.Manifest.Id);
        using JsonDocument document = JsonDocument.Parse(stored);
        foreach (string kind in new[] { "output", "snapshot" })
        foreach (string operation in new[] { "read", "create", "modify", "delete", "rename" })
            Assert.NotEqual("ESCAPED", document.RootElement.GetProperty(kind + "_" + operation).GetString());
        Assert.Equal("ok", document.RootElement.GetProperty("broker_output").GetString());
        Assert.Equal("output-original", File.ReadAllText(outputFile));
        Assert.Equal("snapshot-original", File.ReadAllText(snapshotFile));
        Assert.False(File.Exists(Path.Combine(outputDirectory, "worker-created.txt")));
        Assert.False(File.Exists(Path.Combine(snapshotDirectory, "worker-created.txt")));
    }

    [HostBuildFact]
    public async Task WorkerCrashRemovesHostOutputPartialFile()
    {
        using BedHarness harness = BedHarness.Create("output-crash");
        harness.InstallBed("output-crash");
        Assert.True(await harness.Service.StartRegisteredAsync("org.example.bed-output-crash"));
        PluginRuntimeController controller = Assert.IsType<PluginRuntimeController>(harness.Service.GetOrCreateController("org.example.bed-output-crash"));
        string outputDirectory = Path.Combine(harness.Paths.TempRoot, "HostOutput", controller.Manifest.Id, controller.ActivationId);
        await Assert.ThrowsAnyAsync<Exception>(() => controller.InvokeCommandAsync("crash"));
        await WaitForAsync(() => controller.State == PluginRuntimeState.FaultDisabled, TimeSpan.FromSeconds(20));
        Assert.False(Directory.Exists(outputDirectory));
    }

    [HostBuildFact]
    public async Task MemoryLimitKillsLeakingWorker()
    {
        using BedHarness harness = BedHarness.Create("leak", new BudgetGovernorConfig
        {
            TotalBudgetBytes = 2_000_000_000,
            WarningThresholdBytes = 1_600_000_000,
            PerPluginQuotaBytes = 224 * 1024 * 1024,
        });
        harness.InstallBed("leak");
        await harness.Service.StartRegisteredAsync("org.example.bed-leak");
        await WaitForAsync(() => harness.Service.BuildManagerRows().Any(row => row.Id == "org.example.bed-leak" && row.State == PluginRuntimeState.FaultDisabled), TimeSpan.FromSeconds(40));
    }

    [HostBuildFact]
    public async Task SustainedBacklogDisablesSlowConsumer()
    {
        using BedHarness harness = BedHarness.Create("slow");
        harness.InstallBed("slow-consumer");
        Assert.True(await harness.Service.StartRegisteredAsync("org.example.bed-slow-consumer"));
        await Task.Delay(1_000);
        harness.Environment.PumpRawBlocks(2000, blockSize: 1024);
        await WaitForAsync(() => harness.Service.BuildManagerRows().Any(row => row.Id == "org.example.bed-slow-consumer" && row.State == PluginRuntimeState.FaultDisabled), TimeSpan.FromSeconds(45));
        PluginRegistryEntry entry = harness.Service.Registry.Current.Plugins["org.example.bed-slow-consumer"];
        Assert.NotNull(entry.FaultDisabled);
        Assert.Contains("落后", entry.FaultDisabled!.Reason, StringComparison.Ordinal);
    }

    [HostBuildFact]
    public async Task StoppedPluginRejectsCommandsAndBlocksHaveNoEffect()
    {
        using BedHarness harness = BedHarness.Create("stopped");
        harness.InstallBed("ok");
        Assert.True(await harness.Service.StartRegisteredAsync("org.example.bed-ok"));
        PluginRuntimeController? controller = harness.Service.GetOrCreateController("org.example.bed-ok");
        Assert.NotNull(controller);
        await harness.Service.StopAllAsync();
        Assert.False(await controller!.InvokeCommandAsync("anything"));
        harness.Environment.PumpRawBlocks(10);
        await Task.Delay(500);
        Assert.Equal(PluginRuntimeState.Disabled, harness.Service.BuildManagerRows().Single(row => row.Id == "org.example.bed-ok").State);
    }

    [HostBuildFact]
    public async Task PendingUpdateIsNotSwitchedUntilStartupValidation()
    {
        using BedHarness harness = BedHarness.Create("update");
        harness.InstallBed("ok", "1.0.0");
        harness.InstallBed("ok", "1.1.0");
        harness.Service.Registry.Mutate(data =>
        {
            PluginRegistryEntry entry = data.Plugins["org.example.bed-ok"];
            data.Plugins["org.example.bed-ok"] = entry with
            {
                SelectedVersion = "1.0.0",
                PendingUpdate = new PendingUpdateRecord { Version = "1.1.0", Digest = "digest", PreviousVersion = "1.0.0" },
            };
            return data;
        });

        Assert.Empty(harness.Service.ApplyPendingUpdates());
        Assert.Equal("1.0.0", harness.Service.Registry.Current.Plugins["org.example.bed-ok"].SelectedVersion);
        Assert.NotNull(harness.Service.Registry.Current.Plugins["org.example.bed-ok"].PendingUpdate);
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(250);
        }

        Assert.True(condition(), "Condition was not met within the timeout.");
    }
}
