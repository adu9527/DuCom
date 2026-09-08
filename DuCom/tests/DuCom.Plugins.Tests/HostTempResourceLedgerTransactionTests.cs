using System.Text.Json;
using DuCom.Core.Persistence;
using DuCom.PluginHost.Core;
using Xunit;

namespace DuCom.Plugins.Tests;

public sealed class HostTempResourceLedgerTransactionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ducom-ledger-transactions-{Guid.NewGuid():N}");
    private int _writesUntilFailure = -1;

    private void Write(string path, string content)
    {
        if (_writesUntilFailure == 0) throw new IOException("Injected ledger save failure.");
        if (_writesUntilFailure > 0) _writesUntilFailure--;
        AtomicFileStore.WriteAllText(path, content);
    }

    private HostTempResourceRecord Record() => new()
    {
        ResourceId = "resource",
        PluginId = "plugin",
        ActivationId = "activation",
        Kind = HostTempResourceKind.OutputFile,
        Path = Path.Combine(_root, "output.part"),
        PluginLimitBytes = 100,
    };

    [Fact]
    public void FailedReservationAndReleaseRestoreRecordAndBothBudgets()
    {
        HostTempDiskBudget budget = new(100);
        using HostTempResourceLedger ledger = new(_root, "host", budget, Write);
        ledger.RegisterIntent(Record());
        Assert.True(ledger.TryReserve("resource", 20));
        _writesUntilFailure = 0;
        Assert.Throws<IOException>(() => ledger.TryReserve("resource", 30));
        Assert.Throws<IOException>(() => ledger.ReleaseReservation("resource", 10));
        Assert.Equal(20, Assert.Single(ledger.Snapshot()).ReservedBytes);
        Assert.Equal(20, budget.ReservedBytes);
        Assert.Equal(20, budget.GetPluginReservedBytes("plugin"));
        Assert.Equal(20, Assert.Single(Read("host")).ReservedBytes);
        _writesUntilFailure = -1;
        Assert.True(ledger.TryCleanup("resource"));
        Assert.Equal(0, budget.ReservedBytes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void CleanupSaveFailureRetainsChargeAndRetriesWithoutUnderflow(int successfulWrites)
    {
        HostTempDiskBudget budget = new(100);
        using HostTempResourceLedger ledger = new(_root, "host", budget, Write);
        ledger.RegisterIntent(Record());
        Assert.True(ledger.TryReserve("resource", 20));
        File.WriteAllText(Record().Path, "data");
        _writesUntilFailure = successfulWrites;
        Assert.Throws<IOException>(() => ledger.TryCleanup("resource"));
        Assert.Equal(successfulWrites == 0, File.Exists(Record().Path));
        ledger.RetryPendingCleanups();
        ledger.RetryPendingCleanups();
        Assert.True(Assert.Single(ledger.Snapshot()).CleanupPending);
        Assert.Equal(20, budget.ReservedBytes);
        Assert.Equal(20, budget.GetPluginReservedBytes("plugin"));
        _writesUntilFailure = -1;
        ledger.RetryPendingCleanups();
        ledger.RetryPendingCleanups();
        Assert.Empty(ledger.Snapshot());
        Assert.Empty(Read("host"));
        Assert.Equal(0, budget.ReservedBytes);
        Assert.Equal(0, budget.GetPluginReservedBytes("plugin"));
    }

    [Fact]
    public void FailedRegistrationCanBeRetried()
    {
        using HostTempResourceLedger ledger = new(_root, "host", new(100), Write);
        _writesUntilFailure = 0;
        Assert.Throws<IOException>(() => ledger.RegisterIntent(Record()));
        Assert.Empty(ledger.Snapshot());
        _writesUntilFailure = -1;
        ledger.RegisterIntent(Record());
        Assert.Single(Read("host"));
    }

    [Fact]
    public void DuplicateSourcesWithDeletionFailuresTransferOnceAndRecoverAfterRestart()
    {
        Seed("old", 20);
        Seed("duplicate", 30);
        File.WriteAllText(Record().Path, "data");
        using FileStream resourceLock = new(Record().Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        string oldPath = LedgerPath("old");
        using (FileStream sourceLock = new(oldPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            HostTempDiskBudget budget = new(100);
            using HostTempResourceLedger owner = new(_root, "owner", budget);
            Assert.Equal(30, budget.ReservedBytes);
            Assert.Equal(30, Assert.Single(owner.Snapshot()).ReservedBytes);
            Assert.Single(Read("owner"));
            Assert.True(File.Exists(oldPath));
            Assert.False(File.Exists(LedgerPath("duplicate")));
            using HostTempResourceLedger concurrent = new(_root, "concurrent", budget);
            Assert.Empty(concurrent.Snapshot());
            Assert.Equal(30, budget.ReservedBytes);
        }

        HostTempDiskBudget restartedBudget = new(100);
        using HostTempResourceLedger restarted = new(_root, "restarted", restartedBudget);
        Assert.Equal(30, restartedBudget.ReservedBytes);
        Assert.Single(restarted.Snapshot());
        Assert.False(File.Exists(oldPath));
        Assert.False(File.Exists(LedgerPath("owner")));
        restarted.RetryPendingCleanups();
        Assert.Equal(30, restartedBudget.ReservedBytes);
        resourceLock.Dispose();
        restarted.RetryPendingCleanups();
        Assert.Empty(restarted.Snapshot());
        Assert.Equal(0, restartedBudget.ReservedBytes);
        Assert.Equal(0, restartedBudget.GetPluginReservedBytes("plugin"));
        Assert.False(File.Exists(Record().Path));
    }

    [Fact]
    public void FailedAdoptionSavePreservesSourcesAndReleasesBudgetAndLocks()
    {
        Seed("old", 20);
        File.WriteAllText(Record().Path, "data");
        using FileStream resourceLock = new(Record().Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        HostTempDiskBudget budget = new(100);
        _writesUntilFailure = 0;
        Assert.Throws<IOException>(() => new HostTempResourceLedger(_root, "owner", budget, Write));
        Assert.Equal(20, Assert.Single(Read("old")).ReservedBytes);
        Assert.Equal(0, budget.ReservedBytes);
        Assert.Equal(0, budget.GetPluginReservedBytes("plugin"));
        _writesUntilFailure = -1;
        using HostTempResourceLedger retry = new(_root, "owner", budget, Write);
        Assert.Equal(20, budget.ReservedBytes);
        Assert.Single(Read("owner"));
        Assert.False(File.Exists(LedgerPath("old")));
    }

    private string LedgerPath(string host) => Path.Combine(_root, "Ledgers", host + ".json");

    [Fact]
    public void PartialAdoptionQuotaFailureRollsBackEarlierChargesWithoutLosingSources()
    {
        HostTempResourceRecord first = Record() with { ReservedBytes = 60 };
        HostTempResourceRecord second = first with { ResourceId = "second", Path = Path.Combine(_root, "second.part") };
        AtomicFileStore.WriteAllText(LedgerPath("old"), JsonSerializer.Serialize(new[] { first, second }));
        File.WriteAllText(first.Path, "first");
        File.WriteAllText(second.Path, "second");
        using FileStream firstLock = new(first.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using FileStream secondLock = new(second.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        HostTempDiskBudget budget = new(100);
        Assert.Throws<InvalidOperationException>(() => new HostTempResourceLedger(_root, "owner", budget));
        Assert.Equal(0, budget.ReservedBytes);
        Assert.Equal(0, budget.GetPluginReservedBytes("plugin"));
        Assert.Equal(2, Read("old").Length);
        Assert.False(File.Exists(LedgerPath("owner")));
        firstLock.Dispose();
        using HostTempResourceLedger retry = new(_root, "owner", budget);
        Assert.Equal("second", Assert.Single(retry.Snapshot()).ResourceId);
        Assert.Equal(60, budget.ReservedBytes);
    }

    [Fact]
    public void DisposeSaveFailureStillReleasesOwnershipLocks()
    {
        HostTempResourceLedger ledger = new(_root, "host", new(100), Write);
        ledger.RegisterIntent(Record());
        _writesUntilFailure = 0;
        Assert.Throws<IOException>(ledger.Dispose);
        _writesUntilFailure = -1;
        using HostTempResourceLedger recovered = new(_root, "recovered", new(100));
        Assert.False(File.Exists(LedgerPath("host")));
        Assert.Empty(recovered.Snapshot());
    }

    private HostTempResourceRecord[] Read(string host) =>
        JsonSerializer.Deserialize<HostTempResourceRecord[]>(File.ReadAllText(LedgerPath(host)))!;

    private void Seed(string host, long bytes) => AtomicFileStore.WriteAllText(
        LedgerPath(host), JsonSerializer.Serialize(new[] { Record() with { ReservedBytes = bytes, CleanupPending = true } }));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
