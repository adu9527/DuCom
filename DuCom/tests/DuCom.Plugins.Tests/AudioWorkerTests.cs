using System.Text.Json;
using DuCom.Plugin;
using DuCom.PluginHost;
using DuCom.PluginHost.Core;
using DuCom.PluginHost.Packages;
using DuCom.PluginHost.Registry;
using DuCom.Services.Plugins;
using Xunit;

namespace DuCom.Plugins.Tests;

[Collection("WorkerLifecycle")]
public sealed class AudioWorkerTests
{
    [HostBuildFact]
    public async Task RealSandboxedPluginLoadsVoicesSavesAudioAndRecoversFromBadProxy()
    {
        if (Environment.GetEnvironmentVariable("DUCOM_SPEECH_LIVE_TEST") != "1") return;
        AudioEnvironment environment = new();
        await using BedHarness harness = BedHarness.Create("audio", environment: environment);
        FactoryPackDefinition pack = Assert.Single(PluginSystemHost.BuildFactoryPacks(), p => p.PluginId == "com.ducom.audio");
        string target = Path.Combine(harness.Paths.InstalledRoot, pack.PluginId, pack.Version);
        Directory.CreateDirectory(target);
        foreach (FactoryPackFile file in pack.Files) File.WriteAllBytes(Path.Combine(target, file.RelativePath), file.Content());
        Assert.True(PluginManifestValidator.TryParseStrict(File.ReadAllText(Path.Combine(target, "plugin.manifest.json")), out PluginManifest? manifest, out string? error), error);
        Assert.True(PluginManifestValidator.Validate(manifest!, _ => true, out var errors), string.Join("; ", errors));
        harness.Service.Registry.Mutate(data => data.Plugins[pack.PluginId] = new PluginRegistryEntry
        {
            Id = pack.PluginId, SelectedVersion = pack.Version, Enabled = true, ApprovedPermissions = [.. manifest!.Permissions],
            InstalledVersions = [new() { Version = pack.Version, Path = target, Digest = PluginPackageInstaller.ComputeDirectoryDigest(target) }],
        });
        PluginRuntimeController? candidate = harness.Service.GetOrCreateController(pack.PluginId);
        Assert.True(candidate is not null, JsonSerializer.Serialize(harness.Service.BuildManagerRows()));
        PluginRuntimeController controller = candidate!;
        Assert.True(await harness.Service.StartRegisteredAsync(pack.PluginId), string.Join("\n", controller.DiagnosticsSnapshot.Select(d => d.Message)) + "\n" + string.Join("\n", environment.FaultNotices.Select(n => n.Reason)));
        Assert.True(await controller.InvokeCommandAsync("open"));
        Assert.True(await controller.InvokeCommandAsync("voices"));
        await WaitFor(environment, s => !s.GetProperty("Busy").GetBoolean() && s.GetProperty("VoiceRevision").GetInt32() > 0);
        Assert.True(await controller.InvokeCommandAsync("generate", new Dictionary<string, string>
        {
            ["text"] = string.Concat(Enumerable.Repeat("你好，这是 DuCom 插件长文分块合成测试。", 20)),
            ["voice"] = "zh-CN-XiaoxiaoNeural", ["filename"] = "集成测试", ["rate"] = "100",
        }));
        await WaitFor(environment, s => !s.GetProperty("Busy").GetBoolean() && s.GetProperty("LastFile").GetString() is { Length: > 0 });
        using (JsonDocument state = JsonDocument.Parse(environment.LastJson))
        {
            Assert.False(state.RootElement.GetProperty("Error").GetBoolean(), state.RootElement.GetProperty("Status").GetString());
            string path = state.RootElement.GetProperty("LastFile").GetString()!;
            Assert.True(new FileInfo(path).Length > 1000);
            Assert.True(File.Exists(Path.ChangeExtension(path, ".srt")));
        }
        foreach (bool mp3 in new[] { true, false })
        {
            string filename = mp3 ? "仅音频测试" : "仅字幕测试";
            Assert.True(await controller.InvokeCommandAsync("generate", new Dictionary<string, string>
            {
                ["text"] = "这是独立输出格式测试。", ["filename"] = filename,
                ["mp3"] = mp3 ? "true" : "false", ["srt"] = mp3 ? "false" : "true",
            }));
            await WaitFor(environment, s => !s.GetProperty("Busy").GetBoolean() && s.GetProperty("Status").GetString() == "合成完成 · " + (mp3 ? "MP3" : "SRT"));
            string stem = Path.Combine(environment.GetLogDirectory(), "DuAudio_" + filename);
            Assert.Equal(mp3, File.Exists(stem + ".mp3"));
            Assert.Equal(!mp3, File.Exists(stem + ".srt"));
        }
        Assert.True(await controller.InvokeCommandAsync("preview", new Dictionary<string, string> { ["text"] = "网络错误测试", ["proxy"] = "file:///bad" }));
        await WaitFor(environment, s => !s.GetProperty("Busy").GetBoolean() && s.GetProperty("Error").GetBoolean());
        Assert.Equal(PluginRuntimeState.Active, controller.State);
        Assert.True(await controller.InvokeCommandAsync("voices"));
        await WaitFor(environment, s => !s.GetProperty("Busy").GetBoolean() && !s.GetProperty("Error").GetBoolean());
        Assert.True(await controller.InvokeCommandAsync("generate", new Dictionary<string, string> { ["text"] = new string('测', 20_000) }));
        Assert.True(await controller.InvokeCommandAsync("stop"));
        await WaitFor(environment, s => !s.GetProperty("Busy").GetBoolean());
        Assert.Equal(PluginRuntimeState.Active, controller.State);
    }
    private static async Task WaitFor(AudioEnvironment environment, Func<JsonElement, bool> condition)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(110));
        while (true)
        {
            if (environment.LastJson is { Length: > 0 } json)
            {
                using JsonDocument state = JsonDocument.Parse(json);
                if (condition(state.RootElement)) return;
                if (!state.RootElement.GetProperty("Busy").GetBoolean() && state.RootElement.GetProperty("Error").GetBoolean())
                    throw new InvalidOperationException(state.RootElement.GetProperty("Status").GetString());
            }
            await Task.Delay(50, timeout.Token);
        }
    }
    private sealed class AudioEnvironment : FakeEnvironment
    {
        private readonly string _output = Path.Combine(Path.GetTempPath(), "ducom-audio-live-" + Guid.NewGuid().ToString("N"));
        private string _lastJson = "";
        public string LastJson => Volatile.Read(ref _lastJson);
        public override string GetLogDirectory() => _output;
        public override void UpdateToolPage(string pluginId, string contributionId, IReadOnlyList<UiNode> nodes)
        {
            Assert.True(UiContributionValidator.Validate(nodes, out string? error), error);
            Volatile.Write(ref _lastJson, nodes.OfType<UiTextNode>().Single(n => n.FieldId == "native-state").Text);
        }
    }
}
