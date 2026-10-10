using Xunit;

namespace DuCom.App.Tests;

public sealed class SingleInstanceLockTests
{
    [Fact]
    public void ExistingMutexWithoutOwnerAllowsStartup()
    {
        string name = $@"Local\DuCom.Tests.SingleInstance.{Guid.NewGuid():N}";
        using Mutex existing = new(false, name);

        Assert.True(Program.TryAcquireSingleInstanceLock(name, out Mutex? acquired));
        Assert.NotNull(acquired);
        acquired.ReleaseMutex();
        acquired.Dispose();
    }

    [Fact]
    public void LiveOwnerBlocksStartupUntilItReleasesLock()
    {
        string name = $@"Local\DuCom.Tests.SingleInstance.{Guid.NewGuid():N}";
        using Mutex existing = new(false, name);
        using ManualResetEventSlim acquired = new();
        using ManualResetEventSlim release = new();
        Thread owner = new(() =>
        {
            existing.WaitOne();
            acquired.Set();
            release.Wait();
            existing.ReleaseMutex();
        });
        owner.Start();
        try
        {
            Assert.True(acquired.Wait(TimeSpan.FromSeconds(10)));
            Assert.False(Program.TryAcquireSingleInstanceLock(name, out Mutex? rejected));
            Assert.Null(rejected);
        }
        finally
        {
            release.Set();
            owner.Join();
        }

        Assert.True(Program.TryAcquireSingleInstanceLock(name, out Mutex? restarted));
        Assert.NotNull(restarted);
        restarted.ReleaseMutex();
        restarted.Dispose();
    }

    [Fact]
    public void AbandonedMutexAllowsRestart()
    {
        string name = $@"Local\DuCom.Tests.SingleInstance.{Guid.NewGuid():N}";
        using Mutex existing = new(false, name);
        Thread previousOwner = new(() => existing.WaitOne());
        previousOwner.Start();
        previousOwner.Join();

        Assert.True(Program.TryAcquireSingleInstanceLock(name, out Mutex? restarted));
        Assert.NotNull(restarted);
        restarted.ReleaseMutex();
        restarted.Dispose();
    }
}
