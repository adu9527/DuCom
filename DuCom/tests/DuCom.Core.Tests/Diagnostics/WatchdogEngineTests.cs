using DuCom.Core.Diagnostics;
using DuCom.Core.Storage;
using Xunit;

namespace DuCom.Core.Tests.Diagnostics;

/// <summary>
/// Engine-level lifecycle behavior shared by the watchdog and variable monitor: session
/// add/remove concurrency across ticks, regex-timeout surfacing, and the
/// trigger-then-dispose pattern executed through the periodic worker.
/// </summary>
public sealed class WatchdogEngineTests
{
    [Fact]
    public void TickSynchronizesContextsWithAddedAndRemovedSessions()
    {
        DateTimeOffset t0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        WatchdogEngine engine = new();
        engine.UpdateRules([Rule("heartbeat", "alive", expectSeconds: 3, throttle: 60)]);

        // Tick 1 establishes the context anchored at the session's current end.
        WatchdogTrigger[] first = [.. engine.Tick([Session("COM1", [])], t0)];
        Assert.Empty(first);
        Assert.Equal(1, engine.ActiveContextCount);

        // Tick 2 (1s later) delivers the heartbeat line: expectation satisfied.
        WatchdogTrigger[] matched = [.. engine.Tick([Session("COM1", ["alive"])], t0.AddSeconds(1))];
        Assert.Empty(matched);

        // Tick 3 (5s after the anchor, no new lines): the 3s window elapses and it fires.
        WatchdogTrigger[] fired = [.. engine.Tick([Session("COM1", [])], t0.AddSeconds(5))];
        Assert.Single(fired);
        Assert.Equal("COM1", fired[0].PortName);

        // Session removed: no contexts remain.
        WatchdogTrigger[] afterRemove = [.. engine.Tick([], t0.AddSeconds(30))];
        Assert.Empty(afterRemove);
        Assert.Equal(0, engine.ActiveContextCount);

        // Re-added session starts a fresh expectation window anchored at the new start
        // (its heartbeat line carries a fresh timestamp).
        WatchdogTrigger[] reopened = [.. engine.Tick([Session("COM1", t0.AddSeconds(60), ["alive"])], t0.AddSeconds(60))];
        Assert.Empty(reopened);
        Assert.Equal(1, engine.ActiveContextCount);
    }

    [Fact]
    public void ClosedSessionsAreDroppedFromContexts()
    {
        WatchdogEngine engine = new();
        WatchdogSessionProbe open = Session("COM1", []);
        WatchdogSessionProbe closed = open with { IsOpen = false };

        engine.Tick([open], DateTimeOffset.UtcNow);
        Assert.Equal(1, engine.ActiveContextCount);

        engine.Tick([closed], DateTimeOffset.UtcNow);
        Assert.Equal(0, engine.ActiveContextCount);
    }

    [Fact]
    public void SessionsChangingAcrossConcurrentTicksKeepIndependentContexts()
    {
        WatchdogEngine engine = new();
        engine.UpdateRules([Rule("r", "data", 60, 60)]);

        // Interleaved tick sequences with changing session sets: no exceptions, contexts
        // follow the open set each tick.
        for (int index = 0; index < 10; index++)
        {
            List<WatchdogSessionProbe> sessions = [Session($"COM{index % 3}", [])];
            IReadOnlyList<WatchdogTrigger> triggers = engine.Tick(sessions, DateTimeOffset.UtcNow);
            Assert.Empty(triggers);
        }

        Assert.Equal(1, engine.ActiveContextCount); // last tick had exactly one session
    }

    [Fact]
    public void RegexTimeoutIsSurfaced()
    {
        WatchdogEngine engine = new();
        engine.UpdateRules([new WatchdogRule(
            Guid.NewGuid(), "catastrophic", @"(?<x>a+)+b", WatchdogMatchMode.Regex,
            IsCaseSensitive: false, IsEnabled: true, ExpectWithinSeconds: 60, ThrottleSeconds: 60,
            WatchdogActionKind.Hint, "")]);

        string longInput = new('a', 30_000);
        engine.Tick([Session("COM1", [longInput])], DateTimeOffset.UtcNow);

        Assert.True(engine.HasRegexTimedOut);
    }

    [Fact]
    public async Task TriggerThenImmediateDisposeCancelsTheAction()
    {
        bool sendStarted = false;
        bool sendObservedCancellation = false;
        using SemaphoreSlim sendGate = new(0);
        Func<CancellationToken, Task> sendAction = async cancellationToken =>
        {
            Volatile.Write(ref sendStarted, true);
            try
            {
                await sendGate.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Volatile.Write(ref sendObservedCancellation, true);
                throw;
            }
        };

        // The engine fires when the expectation window elapses; the app-level service runs
        // the action inside the worker tick, so disposing the worker cancels it mid-flight.
        WatchdogEngine engine = new();
        engine.UpdateRules([Rule("send", "x", 1, 60)]);
        WatchdogSessionProbe probe = new("COM1", true, _ => new LineStoreSnapshot(null, null, 0, []));

        List<Exception> logged = [];
        await using PeriodicBackgroundWorker worker = new(
            "watchdog-test",
            TimeSpan.FromMilliseconds(20),
            async cancellationToken =>
            {
                IReadOnlyList<WatchdogTrigger> fired = engine.Tick([probe], DateTimeOffset.UtcNow);
                foreach (WatchdogTrigger _ in fired)
                {
                    await sendAction(cancellationToken);
                }
            },
            (_, exception) =>
            {
                lock (logged)
                {
                    if (exception is not OperationCanceledException)
                    {
                        logged.Add(exception);
                    }
                }
            });
        worker.Start();

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        while (!Volatile.Read(ref sendStarted))
        {
            await Task.Delay(10, timeout.Token);
        }

        await worker.DisposeAsync();
        sendGate.Release(); // unblock if the cancellation path missed it
        Assert.True(Volatile.Read(ref sendObservedCancellation), "the in-flight action must observe cancellation");
        Assert.Empty(logged);
    }

    private static WatchdogRule Rule(string name, string pattern, int expectSeconds, int throttle) => new(
        Guid.NewGuid(), name, pattern, WatchdogMatchMode.Contains,
        IsCaseSensitive: false, IsEnabled: true, ExpectWithinSeconds: expectSeconds,
        ThrottleSeconds: throttle, WatchdogActionKind.SendCommand, "RESET");

    /// <summary>
    /// A session probe whose first pull (context anchoring) returns nothing and each later
    /// pull returns the next batch — mirroring how real cursored snapshots deliver data.
    /// Line timestamps use the given base so engine timing is fully deterministic.
    /// </summary>
    private static WatchdogSessionProbe Session(string port, DateTimeOffset timestampBase, params string[][] batches)
    {
        long id = 0;
        Queue<string[]> pending = new(batches);
        int pulls = 0;
        return new WatchdogSessionProbe(port, IsOpen: true, _ =>
        {
            if (Interlocked.Increment(ref pulls) == 1 || !pending.TryDequeue(out string[]? batch) || batch.Length == 0)
            {
                return new LineStoreSnapshot(1, id, 0, []);
            }

            List<StoredLine> stored = [.. batch.Select(text => new StoredLine(++id, 0, LineDirection.Rx, timestampBase, text, true))];
            return new LineStoreSnapshot(1, id, 0, stored);
        });
    }

    private static WatchdogSessionProbe Session(string port, params string[][] batches) =>
        Session(port, TimestampBase, batches);

    private static readonly DateTimeOffset TimestampBase = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
}

public sealed class VariableMonitorEngineTests
{
    [Fact]
    public void TickExtractsFirstCaptureGroupPerPort()
    {
        VariableMonitorEngine engine = new();
        engine.UpdateRules([new VariableMonitorRule(Guid.NewGuid(), "temp", "COM1", @"T=(\d+)", true, 0)]);

        engine.Tick([MonitorProbe(
            "COM1",
            ["T=21", "T=42"],
            batch =>
            {
                List<StoredLine> stored = [.. batch.Select(text => new StoredLine(1, 0, LineDirection.Rx, DateTimeOffset.UtcNow, text, true))];
                return new LineStoreSnapshot(1, stored.Count, 0, stored);
            })]);

        IReadOnlyList<(VariableMonitorRule Rule, VariableMonitorSample? Sample)> states = engine.GetRuleStates();
        VariableMonitorSample? sample = Assert.Single(states).Sample;
        Assert.NotNull(sample);
        Assert.Equal("42", sample!.Value);
        Assert.Equal(2, sample.MatchCount);
    }

    [Fact]
    public void RemovedSessionsStopContributingLines()
    {
        VariableMonitorEngine engine = new();
        engine.UpdateRules([new VariableMonitorRule(Guid.NewGuid(), "any", null, @"(\d+)", true, 0)]);

        VariableMonitorSessionProbe probe = MonitorProbe(
            "COM1",
            ["v=7"],
            batch =>
            {
                List<StoredLine> stored = [.. batch.Select(text => new StoredLine(1, 0, LineDirection.Rx, DateTimeOffset.UtcNow, text, true))];
                return new LineStoreSnapshot(1, stored.Count, 0, stored);
            });

        engine.Tick([probe]);
        engine.Tick([probe]); // no new batch: cursor pull returns nothing new
        engine.Tick([]);      // session removed

        IReadOnlyList<(VariableMonitorRule Rule, VariableMonitorSample? Sample)> states = engine.GetRuleStates();
        Assert.Single(states);
        Assert.Equal(1, states[0].Sample?.MatchCount);
    }

    /// <summary>Monitor probe whose first pull anchors the cursor and later pulls deliver batches.</summary>
    private static VariableMonitorSessionProbe MonitorProbe(string port, string[] batch, Func<string[], LineStoreSnapshot> deliver)
    {
        Queue<string[]> batches = new([batch]);
        int pulls = 0;
        return new VariableMonitorSessionProbe(port, true, _ =>
            Interlocked.Increment(ref pulls) == 1 || !batches.TryDequeue(out string[]? next)
                ? new LineStoreSnapshot(null, null, 0, [])
                : deliver(next!));
    }
}
