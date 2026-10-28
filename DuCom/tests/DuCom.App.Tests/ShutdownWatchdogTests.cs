using DuCom.Services;
using Xunit;

namespace DuCom.App.Tests;

public sealed class ShutdownWatchdogTests
{
    [Fact]
    public void BlockedCleanupTriggersFallbackOnlyOnce()
    {
        using ManualResetEventSlim fired = new();
        int calls = 0;
        ShutdownWatchdog guard = new(TimeSpan.FromMilliseconds(50), () =>
        {
            Interlocked.Increment(ref calls);
            fired.Set();
        });
        guard.Start();
        guard.Start();
        Assert.True(fired.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, Volatile.Read(ref calls));
    }

    [Fact]
    public void CompletedCleanupDoesNotTerminateProcess()
    {
        using ManualResetEventSlim fired = new();
        ShutdownWatchdog guard = new(TimeSpan.FromMilliseconds(50), () => fired.Set());
        guard.Complete();
        guard.Start();
        Assert.False(fired.Wait(TimeSpan.FromMilliseconds(200)));
    }
}
