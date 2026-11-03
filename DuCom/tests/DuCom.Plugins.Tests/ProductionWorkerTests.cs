using System.Collections.Concurrent;
using System.Text.Json;
using DuCom.Plugin;
using DuCom.PluginHost;
using DuCom.PluginHost.Core;
using DuCom.PluginHost.Packages;
using DuCom.PluginHost.Registry;
using DuCom.Plugins.Production;
using DuCom.Services.Plugins;
using Xunit;

namespace DuCom.Plugins.Tests;

[Collection("WorkerLifecycle")]
public sealed class ProductionWorkerTests
{
    [HostBuildFact]
    public async Task EmbeddedWorkerRunsSerialSequenceWithoutPortsAndStopsLoop()
    {
        ProductionEnvironment environment = new();
        await using BedHarness harness = BedHarness.Create("production", environment: environment);
        FactoryPackDefinition pack = Assert.Single(PluginSystemHost.BuildFactoryPacks(), p => p.PluginId == "com.ducom.production");
        string target = Path.Combine(harness.Paths.InstalledRoot, pack.PluginId, pack.Version);
        Directory.CreateDirectory(target);
        foreach (FactoryPackFile file in pack.Files)
        {
            string path = Path.Combine(target, file.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, file.Content());
        }
        harness.Service.Registry.Mutate(data => data.Plugins[pack.PluginId] = new PluginRegistryEntry
        {
            Id = pack.PluginId, SelectedVersion = pack.Version, Enabled = true,
            ApprovedPermissions = [Permission.SerialLease, Permission.StorageOwn],
            InstalledVersions = [new() { Version = pack.Version, Path = target, Digest = PluginPackageInstaller.ComputeDirectoryDigest(target) }],
        });
        PluginRuntimeController controller = Assert.IsType<PluginRuntimeController>(harness.Service.GetOrCreateController(pack.PluginId));
        Assert.True(await harness.Service.StartRegisteredAsync(pack.PluginId));
        ProductionProfile profile = ProductionProfile.CreateExample();
        profile.Commands[0].DelayMs = 60;
        Assert.True(await controller.InvokeCommandAsync("apply", new Dictionary<string, string> { ["profile"] = profile.ToJson() }));
        Assert.True(await controller.InvokeCommandAsync("once", new Dictionary<string, string> { ["profile"] = profile.ToJson() }));
        await WaitFor(environment, root => root.GetProperty("Status").GetString()!.StartsWith("完成", StringComparison.Ordinal));
        using (JsonDocument state = JsonDocument.Parse(environment.LastJson))
        {
            var results = state.RootElement.GetProperty("Results");
            Assert.Equal(4, results.EnumerateObject().Count());
            Assert.Contains("左：未连接 · 右：未连接", results.GetProperty(profile.Commands[0].Id).GetString());
            Assert.False(results.TryGetProperty("pair-left", out _));
            string record = state.RootElement.GetProperty("RecordPath").GetString()!;
            Assert.True(File.Exists(record + ".log")); Assert.True(File.Exists(record + ".csv"));
            Assert.Contains("不通过", File.ReadAllText(record + ".csv"));
            Assert.Equal(1, state.RootElement.GetProperty("Failed").GetInt32());
        }
        Assert.True(await controller.InvokeCommandAsync("loop", new Dictionary<string, string> { ["profile"] = profile.ToJson() }));
        await WaitFor(environment, root => root.GetProperty("Running").GetBoolean());
        Assert.True(await controller.InvokeCommandAsync("stop", new Dictionary<string, string>()));
        await WaitFor(environment, root => !root.GetProperty("Running").GetBoolean() && root.GetProperty("Status").GetString() == "已停止");
        Assert.True(await controller.InvokeCommandAsync("clear", new Dictionary<string, string> { ["channel"] = "all" }));
        await WaitFor(environment, root => !root.GetProperty("Results").EnumerateObject().Any() && !root.GetProperty("LeftResults").EnumerateObject().Any() && root.GetProperty("Logs").GetArrayLength() == 0);
        Assert.True(await controller.InvokeCommandAsync("clear-batch", new Dictionary<string, string>()));
        await WaitFor(environment, root => root.GetProperty("Passed").GetInt32() == 0 && root.GetProperty("Failed").GetInt32() == 0 && root.GetProperty("LeftState").GetString() == "待执行");
        Assert.True(await controller.InvokeCommandAsync("close", new Dictionary<string, string>()));
    }

    private static async Task WaitFor(ProductionEnvironment environment, Func<JsonElement, bool> predicate)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        while (true)
        {
            if (environment.LastJson is { Length: > 0 } json)
            {
                using JsonDocument document = JsonDocument.Parse(json);
                if (predicate(document.RootElement)) return;
            }
            await Task.Delay(25, timeout.Token);
        }
    }

    private sealed class ProductionEnvironment : FakeEnvironment
    {
        private string _lastJson = "";
        public string LastJson => Volatile.Read(ref _lastJson);
        public override void UpdateToolPage(string pluginId, string contributionId, IReadOnlyList<UiNode> nodes)
        {
            Assert.True(UiContributionValidator.Validate(nodes, out string? error), error);
            Volatile.Write(ref _lastJson, string.Concat(nodes.OfType<UiTextNode>().Select(n => n.Text)));
        }
    }
}
