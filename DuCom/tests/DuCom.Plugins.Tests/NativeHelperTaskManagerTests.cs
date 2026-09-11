using System.Text.Json;
using DuCom.Plugin;
using DuCom.Plugin.Dto;
using DuCom.PluginHost.Core;
using Xunit;

namespace DuCom.Plugins.Tests;

public sealed class NativeHelperTaskManagerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ducom-helper-{Guid.NewGuid():N}");

    [Theory]
    [InlineData("success", "succeeded")]
    [InlineData("missing", "failed")]
    [InlineData("contradict", "failed")]
    [InlineData("fail", "failed")]
    [InlineData("corrupt-progress", "failed")]
    public async Task ReconcilesResultDocumentAndExitCode(string mode, string expected)
    {
        using NativeHelperTaskManager manager = CreateManager();
        string id = "task-" + Guid.NewGuid().ToString("N");
        manager.Start(Start(id, mode));
        HelperTaskResult result = await WaitTerminal(manager, id);
        Assert.Equal(expected, result.State);
        if (mode == "success") Assert.Equal("ok", result.Result);
    }

    [Fact]
    public async Task CooperativeCancelProducesCancelledTerminalState()
    {
        using NativeHelperTaskManager manager = CreateManager();
        string id = "task-" + Guid.NewGuid().ToString("N");
        manager.Start(Start(id, "hang"));
        HelperTaskResult result = await manager.CancelAsync(id);
        Assert.Equal("cancelled", result.State);
    }

    [Fact]
    public void DuplicateTaskIdDoesNotLaunchAnotherHelper()
    {
        using NativeHelperTaskManager manager = CreateManager();
        string id = "task-" + Guid.NewGuid().ToString("N");
        manager.Start(Start(id, "hang"));
        PluginScopeException error = Assert.Throws<PluginScopeException>(() => manager.Start(Start(id, "success")));
        Assert.Equal(PluginErrorCode.InvalidArgument, error.Code);
    }

    private NativeHelperTaskManager CreateManager()
    {
        string source = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "DuCom.NativeHelperTestBed", "bin", "Debug", "net10.0", "win-x86", "DuCom.NativeHelperTestBed.exe");
        source = Path.GetFullPath(source);
        Assert.True(File.Exists(source), source);
        string package = Path.Combine(_root, "package");
        string helperDirectory = Path.Combine(package, "helpers", "win-x86");
        Directory.CreateDirectory(helperDirectory);
        foreach (string file in Directory.GetFiles(Path.GetDirectoryName(source)!))
            File.Copy(file, Path.Combine(helperDirectory, Path.GetFileName(file)), true);
        PluginManifest manifest = new()
        {
            NativeHelpers = [new PluginNativeHelper { Id = "fake", EntryPoint = "helpers/win-x86/DuCom.NativeHelperTestBed.exe", Rid = "win-x86", ProtocolVersion = "1.0" }],
        };
        return new NativeHelperTaskManager(manifest, package, Path.Combine(_root, "tasks"));
    }

    private static HelperStartRequest Start(string id, string mode) => new()
    {
        HelperId = "fake", TaskId = id, TimeoutMs = 10_000,
        Payload = JsonSerializer.Serialize(new { mode }),
    };

    private static async Task<HelperTaskResult> WaitTerminal(NativeHelperTaskManager manager, string id)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            HelperTaskResult result = manager.Status(id);
            if (result.EndedUtc.HasValue) return result;
            await Task.Delay(25);
        }
        throw new TimeoutException(id);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
    }
}
