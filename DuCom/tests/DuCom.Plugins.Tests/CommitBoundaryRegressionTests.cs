using System.Reflection;
using System.Text.Json;
using DuCom.Plugin;
using DuCom.Plugin.Dto;
using DuCom.PluginHost.Core;
using DuCom.PluginHost.Diagnostics;
using Xunit;

namespace DuCom.Plugins.Tests;

[CollectionDefinition("CommitBoundary", DisableParallelization = true)]
public sealed class CommitBoundaryCollection { }

[Collection("CommitBoundary")]
public sealed class CommitBoundaryRegressionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ducom-commit-boundary-" + Guid.NewGuid().ToString("N"));
    private readonly ActivationScope _scope;
    private readonly PluginBroker _broker;
    private readonly HostTempDiskBudget _budget = new(1024 * 1024);

    public CommitBoundaryRegressionTests()
    {
        Directory.CreateDirectory(_root);
        PluginManifest manifest = new() { Id = "org.example.commit-boundary", Permissions = [Permission.FilesUserSelectedWrite] };
        _scope = new(manifest, "activation", Path.Combine(_root, "storage"), Path.Combine(_root, "scratch"), Path.Combine(_root, "output"), Path.Combine(_root, "snapshots"), new PluginLimits(), manifest.Permissions, _budget);
        _broker = new(_scope, new FakeEnvironment(), new PluginDiagnosticsLog(Path.Combine(_root, "diag.log"), manifest.Id));
    }

    private Task<JsonElement?> Call(string op, object? data = null) =>
        _broker.ExecuteAsync(op, data is null ? null : JsonSerializer.SerializeToElement(data, DtoJson.Options), CancellationToken.None);

    private async Task<string> Output(string text)
    {
        string token = (await Call(PluginOps.OutputBegin))!.Value.Deserialize<OutputBeginResult>(DtoJson.Options)!.Token;
        await Call(PluginOps.OutputWrite, new OutputWriteRequest { Token = token, B64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text)) });
        return token;
    }

    private async Task<string> State(string id) => (await Call(PluginOps.OutputCommitStatus, new OutputCommitStatusRequest { CommitId = id }))!.Value.Deserialize<OutputCommitStatusResult>(DtoJson.Options)!.State;

    [Fact]
    public async Task TwoPreparedOutputsSharingApprovedTargetHaveExactlyOneWinner()
    {
        string path = Path.Combine(_root, "result.bin");
        File.WriteAllText(path, "original");
        string target = _scope.CreateFileGrant(path, false, true, replacePathApproved: true);
        OutputCommitRequest first = new() { Token = await Output("first"), TargetToken = target, CommitId = "first" };
        OutputCommitRequest second = new() { Token = await Output("second"), TargetToken = target, CommitId = "second" };
        using CountdownEvent prepared = new(2);
        using ManualResetEventSlim release = new();
        PluginBroker.FileIoTestHook = (stage, _) =>
        {
            if (stage != "publish.before-commit") return;
            prepared.Signal();
            Assert.True(release.Wait(TimeSpan.FromSeconds(15)));
        };
        Task<Exception?> a = Task.Run(() => Record.ExceptionAsync(() => Call(PluginOps.OutputCommit, first)));
        Task<Exception?> b = Task.Run(() => Record.ExceptionAsync(() => Call(PluginOps.OutputCommit, second)));
        bool bothPrepared = prepared.Wait(TimeSpan.FromSeconds(15));
        release.Set();
        Exception?[] errors = await Task.WhenAll(a, b);
        Assert.True(bothPrepared);
        Assert.Single(errors, error => error is null);
        Assert.Equal(PluginErrorCode.SessionExpired, Assert.IsType<PluginScopeException>(Assert.Single(errors, error => error is not null)).Code);
        string winner = errors[0] is null ? "first" : "second";
        OutputCommitRequest loser = errors[0] is null ? second : first;
        Assert.Equal(winner, File.ReadAllText(path));
        Assert.Equal("committed", await State(winner));
        Assert.Equal("aborted", await State(loser.CommitId));
        Assert.Null(_scope.ResolveToken(target, true));
        await Call(PluginOps.OutputDiscard, new FilesTokenRequest { Token = loser.Token });
        Assert.Equal(0, _budget.ReservedBytes);
    }

    [Fact]
    public async Task PostRenameExceptionCannotEraseSuccessOrReuseTarget()
    {
        string path = Path.Combine(_root, "result.bin");
        string target = _scope.CreateFileGrant(path, false, true, true);
        OutputCommitRequest request = new() { Token = await Output("published"), TargetToken = target, CommitId = "published" };
        PluginBroker.FileIoTestHook = (stage, _) =>
        {
            if (stage != "publish.before-cleanup") return;
            Assert.Equal("committed", State(request.CommitId).GetAwaiter().GetResult());
            Assert.Null(_scope.ResolveToken(target, true));
            throw new IOException("Injected post-rename failure");
        };
        await Call(PluginOps.OutputCommit, request);
        PluginBroker.FileIoTestHook = null;
        Assert.Equal("published", File.ReadAllText(path));
        Assert.Equal("committed", await State(request.CommitId));
        await Call(PluginOps.OutputCommit, request);
        await Assert.ThrowsAsync<PluginScopeException>(() => Call(PluginOps.OutputCommit, new OutputCommitRequest { Token = request.Token, TargetToken = target, CommitId = "reuse" }));
        Assert.Equal("aborted", await State("reuse"));
        await Call(PluginOps.OutputDiscard, new FilesTokenRequest { Token = request.Token });
        Assert.Equal("committed", await State(request.CommitId));
    }

    [Fact]
    public async Task InvalidPreparationRecordsAbortedInsteadOfPermanentPreparing()
    {
        await Assert.ThrowsAsync<PluginScopeException>(() => Call(PluginOps.OutputCommit, new OutputCommitRequest { Token = "missing", TargetToken = "missing", CommitId = "invalid" }));
        Assert.Equal("aborted", await State("invalid"));
    }

    [Fact]
    public async Task CleanupFailureRetainsReservationAndRetryableOutputWithoutAbortingCommit()
    {
        string path = Path.Combine(_root, "result.bin");
        OutputCommitRequest request = new() { Token = await Output("published"), TargetToken = _scope.CreateFileGrant(path, false, true), CommitId = "cleanup" };
        FileStream? blocker = null;
        PluginBroker.FileIoTestHook = (stage, _) =>
        {
            if (stage == "publish.before-cleanup")
                blocker = new FileStream(Assert.Single(Directory.EnumerateFiles(_scope.HostOutputDirectory)), FileMode.Open, FileAccess.Read, FileShare.Read);
        };
        try
        {
            await Call(PluginOps.OutputCommit, request);
            Assert.Equal("committed", await State(request.CommitId));
            Assert.Equal(9, _budget.ReservedBytes);
            await Assert.ThrowsAsync<PluginScopeException>(() => Call(PluginOps.OutputDiscard, new FilesTokenRequest { Token = request.Token }));
        }
        finally { blocker?.Dispose(); PluginBroker.FileIoTestHook = null; }
        await Call(PluginOps.OutputDiscard, new FilesTokenRequest { Token = request.Token });
        Assert.Equal(0, _budget.ReservedBytes);
        Assert.Equal("committed", await State(request.CommitId));
        Assert.Equal("published", File.ReadAllText(path));
    }

    public void Dispose()
    {
        PluginBroker.FileIoTestHook = null;
        _scope.Dispose();
        Directory.Delete(_root, true);
    }
}

[Collection("CommitBoundary")]
public sealed class LogPackageCommitRecoveryRegressionTests
{
    [Theory]
    [InlineData("aborted", "cancel")]
    [InlineData("unknown", "cancel")]
    [InlineData("query-failed", "host")]
    [InlineData("committed", "io")]
    [InlineData("aborted", "io")]
    public async Task PackExceptionResolvesOrRetainsCommitAndAlwaysReleasesSnapshot(string state, string error)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "src", "DuCom.Plugins.LogPackage"))) root = root.Parent;
        Assert.NotNull(root);
        Assembly assembly = Assembly.LoadFrom(Path.Combine(root.FullName, "src", "DuCom.Plugins.LogPackage", "bin", new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name, "net10.0", "DuCom.Plugins.LogPackage.dll"));
        Type type = assembly.GetType("DuCom.Plugins.LogPackage.Plugin", true)!;
        object plugin = Activator.CreateInstance(type)!;
        RecoveryChannel channel = new() { State = state, CommitError = error };
        Type facade = typeof(DuComPlugin).Assembly.GetType("DuCom.Plugin.PluginApiFacades", true)!;
        typeof(DuComPlugin).GetMethod("BindHostApi", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(plugin, [Activator.CreateInstance(facade, channel)!]);
        await (Task)type.GetMethod("RunPackAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(plugin, [new PluginTaskContext("pack", CancellationToken.None)])!;
        Assert.Equal(1, channel.Commits);
        Assert.Equal(1, channel.Releases);
        Assert.Equal(state == "aborted" ? 1 : 0, channel.Discards);
        Assert.Equal(state is "aborted" or "committed", type.GetField("_pendingCommit", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(plugin) is null);
        if (state is "unknown" or "query-failed")
            Assert.Contains("unconfirmed", (string)type.GetField("_status", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(plugin)!);
    }

    [Theory]
    [InlineData("aborted")]
    [InlineData("unknown")]
    [InlineData("preparing")]
    [InlineData("committing")]
    [InlineData("query-failed")]
    [InlineData("committed")]
    [InlineData("discard-failed")]
    public async Task PendingCommitIsOnlyReleasedAfterConfirmedTerminalResult(string state)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "src", "DuCom.Plugins.LogPackage"))) root = root.Parent;
        Assert.NotNull(root);
        Assembly assembly = Assembly.LoadFrom(Path.Combine(root.FullName, "src", "DuCom.Plugins.LogPackage", "bin", new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name, "net10.0", "DuCom.Plugins.LogPackage.dll"));
        Type type = assembly.GetType("DuCom.Plugins.LogPackage.Plugin", true)!;
        object plugin = Activator.CreateInstance(type)!;
        RecoveryChannel channel = new() { State = state };
        Type facade = typeof(DuComPlugin).Assembly.GetType("DuCom.Plugin.PluginApiFacades", true)!;
        object api = Activator.CreateInstance(facade, channel)!;
        typeof(DuComPlugin).GetMethod("BindHostApi", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(plugin, [api]);
        FieldInfo pending = type.GetField("_pendingCommit", BindingFlags.Instance | BindingFlags.NonPublic)!;
        pending.SetValue(plugin, ("commit-id", "output-token"));
        MethodInfo resolve = type.GetMethod("ResolvePendingCommitAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        bool resolved = await (Task<bool>)resolve.Invoke(plugin, null)!;
        bool terminal = state is "aborted" or "committed";
        Assert.Equal(terminal, resolved);
        Assert.Equal(terminal, pending.GetValue(plugin) is null);
        Assert.Equal(state is "aborted" or "discard-failed" ? 1 : 0, channel.Discards);
        if (!terminal)
        {
            Assert.Contains(channel.Diagnostics, text => text.Contains("commit-id") && text.Contains("output-token"));
            string status = (string)type.GetField("_status", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(plugin)!;
            Assert.Contains("unconfirmed", status);
            channel.State = "aborted";
            Assert.True(await (Task<bool>)resolve.Invoke(plugin, null)!);
            Assert.Null(pending.GetValue(plugin));
        }
    }

    private sealed class RecoveryChannel : IPluginRequestChannel
    {
        public string State { get; set; } = "unknown";
        public int Discards { get; private set; }
        public int Commits { get; private set; }
        public int Releases { get; private set; }
        public string? CommitError { get; init; }
        public List<string> Diagnostics { get; } = [];

        public Task<JsonElement?> RequestAsync(string operation, object? request, CancellationToken cancellationToken)
        {
            object result = new { ok = true };
            if (operation == PluginOps.OutputCommitStatus)
            {
                if (State == "query-failed") throw new IOException("Query unavailable");
                result = new OutputCommitStatusResult { State = State == "discard-failed" ? "aborted" : State, FinalPath = "result.zip", Bytes = 42 };
            }
            if (operation == PluginOps.FilesHostPaths) result = new HostPathsResult { LogDirectory = Path.GetTempPath() };
            if (operation == PluginOps.FilesCreateWriteTarget) result = new FilesPickResult { Token = "target", DisplayPath = Path.Combine(Path.GetTempPath(), "result.zip") };
            if (operation == PluginOps.OutputDiscard)
            {
                Discards++;
                Assert.Equal("output-token", Assert.IsType<FilesTokenRequest>(request).Token);
                Assert.False(cancellationToken.IsCancellationRequested);
                if (State == "discard-failed") throw new IOException("Cleanup unavailable");
            }
            if (operation == PluginOps.LogsList) result = new SerialListResult { Sessions = [new SerialSessionInfo { SessionId = "s", Port = "P", Open = true }] };
            if (operation == PluginOps.LogsSnapshot) result = new LogsSnapshotResult { Token = "snapshot", Files = [new LogSnapshotFile { Index = 0, Name = "test.log", Port = "P", Length = 0 }] };
            if (operation == PluginOps.OutputBegin) result = new OutputBeginResult { Token = "output-token", MaxBytes = 1024 * 1024, ChunkMaxBytes = 48 * 1024 };
            if (operation == PluginOps.OutputWrite)
            {
                OutputWriteRequest write = Assert.IsType<OutputWriteRequest>(request);
                result = new OutputWriteResult { Length = write.Offset + Convert.FromBase64String(write.B64).Length };
            }
            if (operation == PluginOps.LogsRelease) Releases++;
            if (operation == PluginOps.OutputCommit)
            {
                Commits++;
                throw CommitError switch
                {
                    "cancel" => new OperationCanceledException(),
                    "host" => new PluginHostException(PluginErrorCode.InternalError, "Response lost"),
                    _ => new IOException("Response lost"),
                };
            }
            return Task.FromResult<JsonElement?>(JsonSerializer.SerializeToElement(result, DtoJson.Options));
        }

        public Task NotifyAsync(string operation, object? payload, CancellationToken cancellationToken)
        {
            Diagnostics.Add(JsonSerializer.Serialize(payload, DtoJson.Options));
            return Task.CompletedTask;
        }
    }
}
