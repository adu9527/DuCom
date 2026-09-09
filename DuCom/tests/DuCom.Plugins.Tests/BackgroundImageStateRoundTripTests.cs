using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DuCom.Plugin;
using DuCom.PluginHost.Core;
using Xunit;

namespace DuCom.Plugins.Tests;

/// <summary>
/// Round-trip regression for the background plugin's live settings: an "apply" must be durable,
/// survive a full worker restart, and the page published by the "open" command must reflect the
/// persisted enabled/opacity state (not a stale snapshot).
/// </summary>
[Collection("WorkerLifecycle")]
public sealed class BackgroundImageStateRoundTripTests
{
    [HostBuildFact]
    public async Task ApplyPersistsAndReopensWithSameState()
    {
        string root = Path.Combine(Path.GetTempPath(), $"ducom-bg-state-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            BackgroundEnvironment environment = new();
            await using BedHarness harness = BedHarness.Create("bg-state", environment: environment);
            PluginRuntimeController controller = InstallBackground(harness);
            Assert.True(await harness.Service.StartRegisteredAsync(controller.Manifest.Id));
            controller = Assert.IsType<PluginRuntimeController>(harness.Service.GetOrCreateController(controller.Manifest.Id));

            // Disable with 70% opacity through the same command the UI slider/checkbox submit.
            Assert.True(await controller.InvokeCommandAsync("apply", new Dictionary<string, string>
            {
                ["enabled"] = "false",
                ["playback"] = "single",
                ["opacity"] = "70",
                ["intervalSeconds"] = "300",
            }));
            await UntilAsync(() => harness.ReadPluginStorage("com.ducom.background-image").Contains("\"enabled\": false"));
            Assert.Contains("\"opacity\": 0.7", harness.ReadPluginStorage("com.ducom.background-image"));

            // Re-enable at 26%: the reopened page must show enabled with the last value.
            Assert.True(await controller.InvokeCommandAsync("apply", new Dictionary<string, string>
            {
                ["enabled"] = "true",
                ["playback"] = "single",
                ["opacity"] = "26",
                ["intervalSeconds"] = "300",
            }));
            await UntilAsync(() => harness.ReadPluginStorage("com.ducom.background-image").Contains("\"enabled\": true"));

            AssertPageShows(environment, enabled: true, opacity: 26);

            // Full worker restart: stop, start again, reopen — the same state must come back.
            await harness.Service.StopAsync("com.ducom.background-image");
            Assert.True(await harness.Service.StartRegisteredAsync("com.ducom.background-image"));
            PluginRuntimeController revived = Assert.IsType<PluginRuntimeController>(harness.Service.GetOrCreateController("com.ducom.background-image"));
            environment.PublishedNodes.Clear();
            Assert.True(await revived.InvokeCommandAsync("open", new Dictionary<string, string>()));
            AssertPageShows(environment, enabled: true, opacity: 26);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static void AssertPageShows(BackgroundEnvironment environment, bool enabled, int opacity)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (environment.PublishedNodes.IsEmpty && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(50);
        }

        IReadOnlyList<UiNode> nodes = environment.PublishedNodes.Last();
        UiCheckBoxNode? enabledNode = nodes.SelectMany(FindNodes).OfType<UiCheckBoxNode>().FirstOrDefault(node => node.FieldId == "enabled");
        UiSliderNode? opacityNode = nodes.SelectMany(FindNodes).OfType<UiSliderNode>().FirstOrDefault(node => node.FieldId == "opacity");
        Assert.NotNull(enabledNode);
        Assert.Equal(enabled, enabledNode!.IsChecked);
        Assert.NotNull(opacityNode);
        Assert.Equal((double)opacity, opacityNode!.Value);
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        Assert.True(condition());
    }

    private static IEnumerable<UiNode> FindNodes(UiNode node)
    {
        yield return node;
        if (node is UiPanelNode panel)
        {
            foreach (UiNode child in panel.Children.SelectMany(FindNodes))
            {
                yield return child;
            }
        }
    }

    private sealed class BackgroundEnvironment : FakeEnvironment
    {
        public System.Collections.Concurrent.ConcurrentQueue<IReadOnlyList<UiNode>> PublishedNodes { get; } = new();

        public override void UpdateToolPage(string pluginId, string contributionId, IReadOnlyList<UiNode> nodes)
        {
            base.UpdateToolPage(pluginId, contributionId, nodes);
            PublishedNodes.Enqueue(nodes);
        }
    }

    private static PluginRuntimeController InstallBackground(BedHarness harness)
    {
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        string sourceDirectory = Path.Combine(FindWorkspaceRoot(), "src", "DuCom.Plugins.BackgroundImage", "bin", configuration, "net10.0");
        string target = Path.Combine(harness.Paths.InstalledRoot, "com.ducom.background-image", "1.0.0");
        Directory.CreateDirectory(target);
        foreach (string file in Directory.EnumerateFiles(sourceDirectory))
            if (Path.GetExtension(file) is ".dll" or ".json") File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true);
        File.Copy(Path.Combine(FindWorkspaceRoot(), "src", "DuCom.Plugins.BackgroundImage", "plugin.manifest.json"), Path.Combine(target, "plugin.manifest.json"), true);
        string digest = DuCom.PluginHost.Packages.PluginPackageInstaller.ComputeDirectoryDigest(target);
        harness.Service.Registry.Mutate(data => data.Plugins["com.ducom.background-image"] = new DuCom.PluginHost.Registry.PluginRegistryEntry
        {
            Id = "com.ducom.background-image", SelectedVersion = "1.0.0", Enabled = true,
            ApprovedPermissions = ["storage.own", "files.user-selected.read"],
            InstalledVersions = [new DuCom.PluginHost.Registry.InstalledVersionRecord { Version = "1.0.0", Path = target, Digest = digest }],
        });
        return Assert.IsType<PluginRuntimeController>(harness.Service.GetOrCreateController("com.ducom.background-image"));
    }

    private static string FindWorkspaceRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "src", "DuCom.Plugins.BackgroundImage"))) return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("DuCom workspace root not found.");
    }
}
