using System.Diagnostics;
using DuCom.PluginHost.Core;
using Xunit;

namespace DuCom.Plugins.Tests;

[Collection("WorkerLifecycle")]
public sealed class PluginServiceWorkerRegressionTests
{
    [HostBuildFact]
    public async Task ServiceStopReturnsOnlyAfterWorkerStops()
    {
        await using BedHarness harness = BedHarness.Create("stop-regression");
        var entry = harness.InstallBed("ok");
        Assert.True(await harness.Service.StartRegisteredAsync(entry.Id));
        PluginRuntimeController controller = Assert.IsType<PluginRuntimeController>(harness.Service.GetOrCreateController(entry.Id));
        using Process process = Process.GetProcessById(checked((int)controller.WorkerPid));
        _ = process.Handle;
        await harness.Service.StopAsync(entry.Id);
        Assert.True(process.HasExited);
        Assert.Equal(PluginRuntimeState.Disabled, controller.State);
    }

    [HostBuildFact]
    public async Task AsyncUninstallWaitsForWorkerExitAndPreservesStorage()
    {
        await using BedHarness harness = BedHarness.Create("uninstall-regression");
        var entry = harness.InstallBed("ok");
        Assert.True(await harness.Service.StartRegisteredAsync(entry.Id));
        PluginRuntimeController controller = Assert.IsType<PluginRuntimeController>(harness.Service.GetOrCreateController(entry.Id));
        using Process process = Process.GetProcessById(checked((int)controller.WorkerPid));
        _ = process.Handle;
        Assert.Throws<InvalidOperationException>(() => harness.Service.Uninstall(entry.Id));
        await harness.Service.UninstallAsync(entry.Id);
        Assert.True(process.HasExited);
        Assert.Contains("marker", harness.ReadPluginStorage(entry.Id));
        Assert.False(Directory.Exists(entry.InstalledVersions.Single().Path));
        Assert.Null(harness.Service.GetOrCreateController(entry.Id));
        Assert.False(await harness.Service.StartRegisteredAsync(entry.Id));
    }
}
