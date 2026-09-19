using System.Text.Json;
using DuCom.PluginHost.Core;
using DuCom.PluginHost.Packages;
using DuCom.PluginHost.Registry;
using DuCom.Plugins.Timer;
using Xunit;

namespace DuCom.Plugins.Tests;

[Collection("WorkerLifecycle")]
public sealed class TimerLifecycleTests
{
    [HostBuildFact]
    public async Task StopPersistsRunningTimerAsPaused()
    {
        await using BedHarness harness = BedHarness.Create("timer-stop");
        PluginRuntimeController controller = InstallTimer(harness);
        Assert.True(await harness.Service.StartRegisteredAsync(controller.Manifest.Id));
        controller = Assert.IsType<PluginRuntimeController>(harness.Service.GetOrCreateController(controller.Manifest.Id));

        Assert.True(await controller.InvokeCommandAsync("toggle-run", new Dictionary<string, string>()));
        await Task.Delay(100);
        await harness.Service.StopAsync(controller.Manifest.Id);

        TimerPluginState? state = JsonSerializer.Deserialize<TimerPluginState>(
            harness.ReadPluginStorage(controller.Manifest.Id), TimerEngine.JsonOptions);
        Assert.NotNull(state?.Current);
        Assert.Equal(StopwatchMode.Paused, state!.Current!.Mode);
        Assert.True(state.Current.AccumulatedMs > 0);
    }

    private static PluginRuntimeController InstallTimer(BedHarness harness)
    {
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        string workspace = FindWorkspaceRoot();
        string sourceDirectory = Path.Combine(workspace, "src", "DuCom.Plugins.Timer", "bin", configuration, "net10.0");
        string target = Path.Combine(harness.Paths.InstalledRoot, "com.ducom.timer", "1.0.0");
        Directory.CreateDirectory(target);
        foreach (string file in Directory.EnumerateFiles(sourceDirectory))
        {
            if (Path.GetExtension(file) is ".dll" or ".json")
            {
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true);
            }
        }

        File.Copy(Path.Combine(workspace, "src", "DuCom.Plugins.Timer", "plugin.manifest.json"), Path.Combine(target, "plugin.manifest.json"), true);
        string digest = PluginPackageInstaller.ComputeDirectoryDigest(target);
        harness.Service.Registry.Mutate(data => data.Plugins["com.ducom.timer"] = new PluginRegistryEntry
        {
            Id = "com.ducom.timer",
            SelectedVersion = "1.0.0",
            Enabled = true,
            ApprovedPermissions = ["storage.own", "files.user-selected.write"],
            InstalledVersions = [new InstalledVersionRecord { Version = "1.0.0", Path = target, Digest = digest }],
        });
        return Assert.IsType<PluginRuntimeController>(harness.Service.GetOrCreateController("com.ducom.timer"));
    }

    private static string FindWorkspaceRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "src", "DuCom.Plugins.Timer")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("DuCom workspace root not found.");
    }
}
