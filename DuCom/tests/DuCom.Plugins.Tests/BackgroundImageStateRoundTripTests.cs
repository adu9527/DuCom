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

    [HostBuildFact]
    public async Task PickingFolderPreservesRandomPlayback()
    {
        using PickerEnvironment environment = new();
        await using BedHarness harness = BedHarness.Create("bg-random-folder", environment: environment);
        PluginRuntimeController controller = InstallBackground(harness);
        Assert.True(await harness.Service.StartRegisteredAsync(controller.Manifest.Id));
        controller = Assert.IsType<PluginRuntimeController>(harness.Service.GetOrCreateController(controller.Manifest.Id));

        Assert.True(await controller.InvokeCommandAsync("apply", new Dictionary<string, string>
        {
            ["playback"] = "random",
        }));
        Assert.True(await controller.InvokeCommandAsync("pick-folder", new Dictionary<string, string>()));

        await UntilAsync(() => environment.AppliedPaths.Any(path => string.Equals(path, environment.ImagePath, StringComparison.OrdinalIgnoreCase)));
        string storage = harness.ReadPluginStorage("com.ducom.background-image");
        Assert.Contains("\"playback\": \"random\"", storage);
        Assert.Contains(environment.FolderPath.Replace("\\", "\\\\"), storage);
    }

    [HostBuildFact]
    public async Task LegacyPercentageOpacityIsNormalizedWhenLoaded()
    {
        BackgroundEnvironment environment = new();
        await using BedHarness harness = BedHarness.Create("bg-opacity-load", environment: environment);
        string storageDirectory = Path.Combine(harness.Paths.StorageRoot, "com.ducom.background-image");
        Directory.CreateDirectory(storageDirectory);
        File.WriteAllText(Path.Combine(storageDirectory, "config.json"), "{\"enabled\":true,\"opacity\":18}");

        PluginRuntimeController controller = InstallBackground(harness);
        Assert.True(await harness.Service.StartRegisteredAsync(controller.Manifest.Id));
        controller = Assert.IsType<PluginRuntimeController>(harness.Service.GetOrCreateController(controller.Manifest.Id));
        Assert.True(await controller.InvokeCommandAsync("open", new Dictionary<string, string>()));

        AssertPageShows(environment, enabled: true, opacity: 18);
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

    private class BackgroundEnvironment : FakeEnvironment
    {
        public System.Collections.Concurrent.ConcurrentQueue<IReadOnlyList<UiNode>> PublishedNodes { get; } = new();

        public override void UpdateToolPage(string pluginId, string contributionId, IReadOnlyList<UiNode> nodes)
        {
            base.UpdateToolPage(pluginId, contributionId, nodes);
            PublishedNodes.Enqueue(nodes);
        }
    }

    private sealed class PickerEnvironment : BackgroundEnvironment, IDisposable
    {
        public PickerEnvironment()
        {
            FolderPath = Path.Combine(Path.GetTempPath(), $"ducom-background-random-{Guid.NewGuid():N}");
            Directory.CreateDirectory(FolderPath);
            ImagePath = Path.Combine(FolderPath, "background.png");
            File.WriteAllBytes(ImagePath, [0x89, 0x50, 0x4E, 0x47]);
        }

        public string FolderPath { get; }

        public string ImagePath { get; }

        public System.Collections.Concurrent.ConcurrentQueue<string> AppliedPaths { get; } = new();

        public override async Task<DuCom.PluginHost.HostPickResult?> PickReadAsync(string pluginId, DuCom.PluginHost.HostPickRequest request, CancellationToken cancellationToken)
        {
            // Exceeds the normal worker command deadline to reproduce a real user taking time
            // in the native folder picker.
            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
            return new DuCom.PluginHost.HostPickResult
            {
                DisplayPath = FolderPath,
                IsDirectory = true,
                Remembered = true,
            };
        }

        public override string? TryResolveRememberedReadPath(string pluginId, string requestedPath)
        {
            string normalized = Path.GetFullPath(requestedPath);
            return string.Equals(normalized, FolderPath, StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith(FolderPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    ? normalized
                    : null;
        }

        public override void ApplyBackground(DuCom.PluginHost.BackgroundApply apply)
        {
            base.ApplyBackground(apply);
            if (apply.ImageTokenPath is not null)
            {
                AppliedPaths.Enqueue(apply.ImageTokenPath);
            }
        }

        public void Dispose()
        {
            try { Directory.Delete(FolderPath, recursive: true); } catch { }
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
