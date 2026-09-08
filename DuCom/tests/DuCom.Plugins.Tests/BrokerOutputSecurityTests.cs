using System.Reflection;
using System.Text;
using System.Text.Json;
using DuCom.Plugin;
using DuCom.Plugin.Dto;
using DuCom.PluginHost;
using DuCom.PluginHost.Core;
using DuCom.PluginHost.Diagnostics;
using Xunit;

namespace DuCom.Plugins.Tests;

public sealed class BrokerOutputSecurityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ducom-output-security-{Guid.NewGuid():N}");
    private readonly List<ActivationScope> _scopes = [];

    private (ActivationScope Scope, PluginBroker Broker) Create(long quota = 1024 * 1024, HostTempDiskBudget? sharedBudget = null)
        => Create("org.example.output-test", quota, sharedBudget);

    private (ActivationScope Scope, PluginBroker Broker) Create(string pluginId, long quota, HostTempDiskBudget? sharedBudget = null)
    {
        PluginManifest manifest = new() { Id = pluginId, Permissions = [Permission.FilesUserSelectedWrite] };
        PluginLimits limits = new() { TempQuotaBytes = quota };
        string activation = Guid.NewGuid().ToString("N");
        ActivationScope scope = new(manifest, activation, Path.Combine(_root, "storage", activation), Path.Combine(_root, "scratch", activation), Path.Combine(_root, "host-output", activation), Path.Combine(_root, "host-snapshots", activation), limits, manifest.Permissions, sharedBudget);
        _scopes.Add(scope);
        return (scope, new PluginBroker(scope, new FakeEnvironment(), new PluginDiagnosticsLog(Path.Combine(_root, "diag.log"), manifest.Id)));
    }

    private (ActivationScope Scope, PluginBroker Broker) CreateForLogs(long quota, HostTempDiskBudget budget, IPluginHostEnvironment environment)
    {
        PluginManifest manifest = new() { Id = "org.example.snapshot-test", Permissions = [Permission.SerialLogsRead] };
        string activation = Guid.NewGuid().ToString("N");
        ActivationScope scope = new(manifest, activation, Path.Combine(_root, "storage", activation), Path.Combine(_root, "scratch", activation), Path.Combine(_root, "host-output", activation), Path.Combine(_root, "host-snapshots", activation), new PluginLimits { TempQuotaBytes = quota }, manifest.Permissions, budget);
        _scopes.Add(scope);
        return (scope, new PluginBroker(scope, environment, new PluginDiagnosticsLog(Path.Combine(_root, "diag.log"), manifest.Id)));
    }

    private static Task<JsonElement?> Call(PluginBroker broker, string op, object? payload = null) =>
        broker.ExecuteAsync(op, payload is null ? null : JsonSerializer.SerializeToElement(payload, DtoJson.Options), CancellationToken.None);

    private static async Task<OutputBeginResult> Begin(PluginBroker broker) =>
        (await Call(broker, PluginOps.OutputBegin))!.Value.Deserialize<OutputBeginResult>(DtoJson.Options)!;

    private static Task<JsonElement?> Write(PluginBroker broker, string token, long offset, string value) =>
        Call(broker, PluginOps.OutputWrite, new OutputWriteRequest { Token = token, Offset = offset, B64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(value)) });

    private string Target(ActivationScope scope, bool allowReplace = false) => scope.CreateFileGrant(Path.Combine(_root, "destination.bin"), false, true, allowReplace);

    [Fact]
    public async Task ExistingTargetRequiresExplicitReplacementApprovalAndRemainsIntactOnFailure()
    {
        var (scope, broker) = Create();
        string destination = Path.Combine(_root, "destination.bin");
        File.WriteAllText(destination, "original");
        OutputBeginResult output = await Begin(broker);
        await Write(broker, output.Token, 0, "new");
        await Denied(broker, PluginOps.OutputCommit, Commit(output.Token, Target(scope)), PluginErrorCode.PermissionDenied);
        Assert.Equal("original", File.ReadAllText(destination));

        string approvedTarget = Target(scope, allowReplace: true);
        FilesCommitResult result = (await Call(broker, PluginOps.OutputCommit, Commit(output.Token, approvedTarget)))!.Value.Deserialize<FilesCommitResult>(DtoJson.Options)!;
        Assert.Equal("new", File.ReadAllText(result.FinalPath));
    }

    private static async Task Denied(PluginBroker broker, string op, object? payload, string code = PluginErrorCode.SessionExpired)
    {
        PluginScopeException error = await Assert.ThrowsAsync<PluginScopeException>(() => Call(broker, op, payload));
        Assert.Equal(code, error.Code);
    }

    [Fact]
    public async Task BeginReturnsNoHostPathAndWritesOnlyThroughOpaqueToken()
    {
        var (scope, broker) = Create();
        OutputBeginResult output = await Begin(broker);
        Assert.False(JsonSerializer.Serialize(output, DtoJson.Options).Contains("path", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(384 * 1024, output.ChunkMaxBytes);
        await Write(broker, output.Token, 0, "expected");
        string target = Target(scope);
        FilesCommitResult result = (await Call(broker, PluginOps.OutputCommit, Commit(output.Token, target)))!.Value.Deserialize<FilesCommitResult>(DtoJson.Options)!;
        Assert.Equal("expected", File.ReadAllText(result.FinalPath));
    }

    [Fact]
    public async Task RejectsUnknownCrossActivationAndOutOfOrderWrites()
    {
        var (_, broker) = Create();
        OutputBeginResult output = await Begin(broker);
        await Denied(broker, PluginOps.OutputWrite, new OutputWriteRequest { Token = "unknown", Offset = 0, B64 = "YQ==" });
        await Write(broker, output.Token, 0, "a");
        await Denied(broker, PluginOps.OutputWrite, new OutputWriteRequest { Token = output.Token, Offset = 0, B64 = "Yg==" }, PluginErrorCode.InvalidArgument);
        await Denied(broker, PluginOps.OutputWrite, new OutputWriteRequest { Token = output.Token, Offset = 2, B64 = "Yg==" }, PluginErrorCode.InvalidArgument);
        var (_, next) = Create();
        await Denied(next, PluginOps.OutputWrite, new OutputWriteRequest { Token = output.Token, Offset = 1, B64 = "Yg==" });
    }

    [Fact]
    public async Task RejectsOversizedAndCumulativeOutputBeforeWriting()
    {
        var (_, broker) = Create(quota: 5);
        OutputBeginResult output = await Begin(broker);
        await Denied(broker, PluginOps.OutputWrite, new OutputWriteRequest { Token = output.Token, Offset = 0, B64 = Convert.ToBase64String(new byte[384 * 1024 + 1]) }, PluginErrorCode.InvalidArgument);
        await Write(broker, output.Token, 0, "12345");
        await Denied(broker, PluginOps.OutputWrite, new OutputWriteRequest { Token = output.Token, Offset = 5, B64 = "YQ==" }, PluginErrorCode.ResourceLimit);
    }

    [Fact]
    public async Task SharedHostDiskBudgetIsAtomicAcrossActivationsAndReleasedOnDiscard()
    {
        HostTempDiskBudget budget = new(5);
        var (_, first) = Create(quota: 10, budget);
        var (_, second) = Create(quota: 10, budget);
        OutputBeginResult firstOutput = await Begin(first);
        OutputBeginResult secondOutput = await Begin(second);
        await Write(first, firstOutput.Token, 0, "12345");
        await Denied(second, PluginOps.OutputWrite, new OutputWriteRequest { Token = secondOutput.Token, Offset = 0, B64 = "YQ==" }, PluginErrorCode.ResourceLimit);
        Assert.Equal(5, budget.ReservedBytes);
        await Call(first, PluginOps.OutputDiscard, new FilesTokenRequest { Token = firstOutput.Token });
        Assert.Equal(0, budget.ReservedBytes);
        await Write(second, secondOutput.Token, 0, "x");
        Assert.Equal(1, budget.ReservedBytes);
    }

    [Fact]
    public async Task SamePluginCannotBypassAggregateQuotaWithMultipleTokens()
    {
        HostTempDiskBudget budget = new(100);
        var (_, first) = Create("org.example.aggregate", 5, budget);
        var (_, second) = Create("org.example.aggregate", 5, budget);
        OutputBeginResult firstOutput = await Begin(first);
        OutputBeginResult secondOutput = await Begin(second);
        await Write(first, firstOutput.Token, 0, "1234");
        await Denied(second, PluginOps.OutputWrite, new OutputWriteRequest { Token = secondOutput.Token, Offset = 0, B64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("12")) }, PluginErrorCode.ResourceLimit);
        Assert.Equal(4, budget.GetPluginReservedBytes("org.example.aggregate"));
    }

    [Fact]
    public async Task TargetAppearingAtCommitPointIsNeverReplacedWithoutApproval()
    {
        var (scope, broker) = Create();
        OutputBeginResult output = await Begin(broker);
        await Write(broker, output.Token, 0, "new");
        string destination = Path.Combine(_root, "destination.bin");
        PluginBroker.FileIoTestHook = (stage, _) =>
        {
            if (stage == "publish.before-commit") File.WriteAllText(destination, "racer");
        };
        try
        {
            await Denied(broker, PluginOps.OutputCommit, Commit(output.Token, Target(scope)), PluginErrorCode.PermissionDenied);
            Assert.Equal("racer", File.ReadAllText(destination));
        }
        finally { PluginBroker.FileIoTestHook = null; }
    }

    [Fact]
    public async Task OpenStagingHandlePreventsPathSubstitutionAndPublishesHostBytes()
    {
        var (scope, broker) = Create();
        OutputBeginResult output = await Begin(broker);
        await Write(broker, output.Token, 0, "host");
        bool replacementDenied = false;
        PluginBroker.FileIoTestHook = (stage, path) =>
        {
            if (stage != "publish.before-commit") return;
            try
            {
                File.Delete(path);
                File.WriteAllText(path, "attacker");
            }
            catch (IOException) { replacementDenied = true; }
            catch (UnauthorizedAccessException) { replacementDenied = true; }
        };
        try
        {
            FilesCommitResult result = (await Call(broker, PluginOps.OutputCommit, Commit(output.Token, Target(scope))))!.Value.Deserialize<FilesCommitResult>(DtoJson.Options)!;
            Assert.True(replacementDenied);
            Assert.Equal("host", File.ReadAllText(result.FinalPath));
        }
        finally { PluginBroker.FileIoTestHook = null; }
    }

    [Fact]
    public async Task CancellationBeforeCommitLeavesTargetUntouchedAndReleasesPublicationQuota()
    {
        HostTempDiskBudget budget = new(1024);
        var (scope, broker) = Create(quota: 1024, budget);
        OutputBeginResult output = await Begin(broker);
        await Write(broker, output.Token, 0, new string('x', 32));
        using CancellationTokenSource cancellation = new();
        PluginBroker.FileIoTestHook = (stage, _) =>
        {
            if (stage == "publish.before-commit") cancellation.Cancel();
        };
        try
        {
            OutputCommitRequest commit = Commit(output.Token, Target(scope));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => broker.ExecuteAsync(
                PluginOps.OutputCommit,
                JsonSerializer.SerializeToElement(commit, DtoJson.Options),
                cancellation.Token));
            Assert.False(File.Exists(Path.Combine(_root, "destination.bin")));
            Assert.Equal(32, budget.ReservedBytes);
            OutputCommitStatusResult status = (await Call(broker, PluginOps.OutputCommitStatus, new OutputCommitStatusRequest { CommitId = commit.CommitId }))!.Value.Deserialize<OutputCommitStatusResult>(DtoJson.Options)!;
            Assert.Equal("aborted", status.State);
        }
        finally { PluginBroker.FileIoTestHook = null; }
    }

    [Fact]
    public async Task SnapshotEarlyEofDeletesPartialAndReturnsAllQuota()
    {
        string log = Path.Combine(_root, "short.log");
        Directory.CreateDirectory(_root);
        File.WriteAllText(log, "123");
        HostTempDiskBudget budget = new(10);
        var (scope, broker) = CreateForLogs(10, budget, new DeclaredSnapshotEnvironment(log, 5));
        await Denied(broker, PluginOps.LogsSnapshot, new LogsSnapshotRequest(), PluginErrorCode.InternalError);
        Assert.Equal(0, budget.ReservedBytes);
        Assert.False(Directory.Exists(scope.HostSnapshotDirectory) && Directory.EnumerateFiles(scope.HostSnapshotDirectory, "*", SearchOption.AllDirectories).Any());
    }

    [Fact]
    public async Task SnapshotReservesBeforeCopyAndReleaseReturnsSharedQuota()
    {
        string log = Path.Combine(_root, "source.log");
        Directory.CreateDirectory(_root);
        File.WriteAllText(log, "12345");
        HostTempDiskBudget budget = new(5);
        var (scope, broker) = CreateForLogs(10, budget, new SnapshotEnvironment(log));
        LogsSnapshotResult result = (await Call(broker, PluginOps.LogsSnapshot, new LogsSnapshotRequest()))!.Value.Deserialize<LogsSnapshotResult>(DtoJson.Options)!;
        Assert.Equal(5, budget.ReservedBytes);
        Assert.Single(Directory.EnumerateFiles(scope.HostSnapshotDirectory, "*", SearchOption.AllDirectories));
        await Call(broker, PluginOps.LogsRelease, new FilesTokenRequest { Token = result.Token });
        Assert.Equal(0, budget.ReservedBytes);
        Assert.Empty(Directory.EnumerateFiles(scope.HostSnapshotDirectory, "*", SearchOption.AllDirectories));
    }

    private sealed class SnapshotEnvironment(string path) : FakeEnvironment
    {
        public override Task<IReadOnlyList<HostLogSnapshot>> CreateLogSnapshotsAsync(string? sessionId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<HostLogSnapshot>>([new HostLogSnapshot("s", "P", [new HostLogSnapshotFile(path, new FileInfo(path).Length, "P", "source.log")])]);
    }

    private sealed class DeclaredSnapshotEnvironment(string path, long length) : FakeEnvironment
    {
        public override Task<IReadOnlyList<HostLogSnapshot>> CreateLogSnapshotsAsync(string? sessionId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<HostLogSnapshot>>([new HostLogSnapshot("s", "P", [new HostLogSnapshotFile(path, length, "P", "source.log")])]);
    }

    [Fact]
    public async Task CommitIsOnceAndRevocationLinearizesAgainstWriteAndCommit()
    {
        var (scope, broker) = Create();
        OutputBeginResult output = await Begin(broker);
        await Write(broker, output.Token, 0, "once");
        string target = Target(scope);
        OutputCommitRequest commit = Commit(output.Token, target);
        FilesCommitResult result = (await Call(broker, PluginOps.OutputCommit, commit))!.Value.Deserialize<FilesCommitResult>(DtoJson.Options)!;
        Assert.Equal("once", File.ReadAllText(result.FinalPath));
        FilesCommitResult repeated = (await Call(broker, PluginOps.OutputCommit, commit))!.Value.Deserialize<FilesCommitResult>(DtoJson.Options)!;
        Assert.Equal(result, repeated);
        OutputCommitStatusResult status = (await Call(broker, PluginOps.OutputCommitStatus, new OutputCommitStatusRequest { CommitId = commit.CommitId }))!.Value.Deserialize<OutputCommitStatusResult>(DtoJson.Options)!;
        Assert.Equal("committed", status.State);
        Assert.Equal(result.FinalPath, status.FinalPath);

        OutputBeginResult pending = await Begin(broker);
        object gate = typeof(ActivationScope).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scope)!;
        Task<JsonElement?> write;
        lock (gate)
        {
            write = Task.Run(() => Call(broker, PluginOps.OutputWrite, new OutputWriteRequest { Token = pending.Token, Offset = 0, B64 = "YQ==" }));
            scope.Revoke();
        }
        await Assert.ThrowsAsync<PluginScopeException>(() => write);
        Assert.False(Directory.Exists(scope.HostOutputDirectory) && Directory.EnumerateFiles(scope.HostOutputDirectory).Any());
    }

    [Fact]
    public async Task DiscardIsIdempotentAndCannotDeleteOtherFiles()
    {
        var (scope, broker) = Create();
        OutputBeginResult output = await Begin(broker);
        await Write(broker, output.Token, 0, "discard");
        string sentinel = Path.Combine(_root, "sentinel.txt");
        File.WriteAllText(sentinel, "keep");
        await Call(broker, PluginOps.OutputDiscard, new FilesTokenRequest { Token = output.Token });
        await Denied(broker, PluginOps.OutputDiscard, new FilesTokenRequest { Token = output.Token });
        Assert.Equal("keep", File.ReadAllText(sentinel));
        Assert.Empty(Directory.Exists(scope.HostOutputDirectory) ? Directory.EnumerateFiles(scope.HostOutputDirectory) : []);
    }

    public void Dispose()
    {
        PluginBroker.FileIoTestHook = null;
        foreach (ActivationScope scope in _scopes) scope.Revoke();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static OutputCommitRequest Commit(string outputToken, string targetToken) => new()
    {
        Token = outputToken,
        TargetToken = targetToken,
        CommitId = Guid.NewGuid().ToString("N"),
    };
}
