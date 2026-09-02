using DuCom.Core.Diagnostics;
using Xunit;

namespace DuCom.Core.Tests.Diagnostics;

/// <summary>
/// Shared periodic worker contract: single-flight skips, exception isolation, and disposal
/// that cancels and waits for the in-flight tick. These guarantees back the watchdog,
/// variable monitor, and Telnet push loop.
/// </summary>
public sealed class PeriodicBackgroundWorkerTests
{
    [Fact]
    public async Task SlowTickBlocksSubsequentTicksNeverOverlaps()
    {
        using SemaphoreSlim firstTickGate = new(0);
        int executions = 0;
        await using PeriodicBackgroundWorker worker = new(
            "test",
            TimeSpan.FromMilliseconds(20),
            async cancellationToken =>
            {
                Interlocked.Increment(ref executions);
                await firstTickGate.WaitAsync(cancellationToken);
            });

        worker.Start();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        while (Volatile.Read(ref executions) < 1)
        {
            await Task.Delay(10, timeout.Token);
        }

        // While the first tick is blocked, no further tick may start (sequential
        // single-flight: the next due period waits for the current tick).
        await Task.Delay(150);
        Assert.Equal(1, Volatile.Read(ref executions));
        Assert.True(worker.IsTickInProgress);

        firstTickGate.Release();
        await worker.DisposeAsync();
        Assert.False(worker.IsTickInProgress);
    }

    [Fact]
    public async Task TickExceptionsAreIsolatedAndLoopContinues()
    {
        List<string> logged = [];
        int executions = 0;
        await using PeriodicBackgroundWorker worker = new(
            "test",
            TimeSpan.FromMilliseconds(20),
            _ =>
            {
                int value = Interlocked.Increment(ref executions);
                if (value == 1)
                {
                    throw new InvalidOperationException("boom");
                }

                return Task.CompletedTask;
            },
            (name, exception) => logged.Add($"{name}:{exception.Message}"));

        worker.Start();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        while (Volatile.Read(ref executions) < 2)
        {
            await Task.Delay(10, timeout.Token);
        }

        Assert.Contains("test:boom", logged);
        await worker.DisposeAsync();
    }

    [Fact]
    public async Task DisposeDuringTickCancelsAndWaitsForTheTick()
    {
        using SemaphoreSlim tickStarted = new(0);
        bool tickObservedCancellation = false;
        await using PeriodicBackgroundWorker worker = new(
            "test",
            TimeSpan.FromMilliseconds(20),
            async cancellationToken =>
            {
                tickStarted.Release();
                try
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    tickObservedCancellation = true;
                    throw;
                }
            });

        worker.Start();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        await tickStarted.WaitAsync(timeout.Token);

        using CancellationTokenSource disposeTimeout = new(TimeSpan.FromSeconds(5));
        await worker.DisposeAsync().AsTask().WaitAsync(disposeTimeout.Token); // must not hang

        Assert.True(tickObservedCancellation);
    }

    [Fact]
    public async Task ConcurrentDisposeSharesOneDisposal()
    {
        await using PeriodicBackgroundWorker worker = new("test", TimeSpan.FromMilliseconds(50), _ => Task.CompletedTask);
        worker.Start();

        ValueTask first = worker.DisposeAsync();
        ValueTask second = worker.DisposeAsync();
        await first;
        await second;
    }
}
