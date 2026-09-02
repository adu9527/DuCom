using DuCom.Core.Sending;
using Xunit;

namespace DuCom.Core.Tests.Sending;

public class ScriptGroupHostTests
{
    private static CommandGroup GroupOf(params ScriptCommand[] commands)
    {
        if (commands.Length == 0)
        {
            commands = [MakeCommand("A")];
        }

        return new CommandGroup(Guid.NewGuid(), "g", commands);
    }

    private static ScriptCommand MakeCommand(string payload, int delayMs = 0) => new(
        Guid.NewGuid(), payload, 0, payload, false, delayMs, false, string.Empty, 5_000, NewlinePolicy.None);

    [Fact]
    public async Task UnsubscribedObserverDoesNotKillHost_SubsequentStartSucceeds()
    {
        // Simulates a tool window opening (subscribe), closing (unsubscribe), and a later
        // window reusing the same shared host to run a group.
        ScriptGroupHost host = new(() => true, (_, _) => Task.CompletedTask);
        List<ScriptCommandState> observed = [];
        void Handler(object? s, EventArgs e) => observed.Add(ScriptCommandState.Pending);
        host.StateChanged += Handler;
        Assert.True(host.Start(GroupOf()));
        await host.StopAsync();
        host.StateChanged -= Handler;

        // Host is still alive and fully usable after the observer left.
        Assert.True(host.Start(GroupOf(MakeCommand("B"))));
        await host.StopAsync();
        Assert.False(host.IsRunning);
    }

    [Fact]
    public async Task StartAfterDisposeThrowsAndSecondDisposeIsImmediate()
    {
        ScriptGroupHost host = new(() => true, (_, _) => Task.CompletedTask);
        await host.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(() => host.Start(GroupOf()));

        ValueTask second = host.DisposeAsync();
        await second;
    }

    [Fact]
    public async Task DisposeCancelsInFlightRunAndWaitsForCompletion()
    {
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ScriptGroupHost host = new(
            () => true,
            async (_, token) =>
            {
                started.SetResult();
                try
                {
                    await Task.Delay(Timeout.Infinite, token);
                }
                catch (OperationCanceledException)
                {
                    cancelled.SetResult();
                    throw;
                }
            });

        Assert.True(host.Start(GroupOf(MakeCommand("loop"))));
        await started.Task;
        Assert.True(host.IsRunning);
        await host.DisposeAsync();
        Assert.True(cancelled.Task.IsCompleted);
        Assert.False(host.IsRunning);
    }

    [Fact]
    public void StartWithoutAdmissionReportsStatusKeyAndStartsNothing()
    {
        List<string> reported = [];
        ScriptGroupHost host = new(
            () => false,
            (_, _) => Task.CompletedTask,
            statusReporter: reported.Add);

        Assert.False(host.Start(GroupOf()));
        Assert.Contains("Status.CommandRunNoSession", reported);
        Assert.False(host.IsRunning);
    }

    [Fact]
    public void StartWithoutAdmissionCanReportSpecificReason()
    {
        List<string> reported = [];
        ScriptGroupHost host = new(
            () => false,
            (_, _) => Task.CompletedTask,
            statusReporter: reported.Add,
            startRejectedStatusKey: "Status.CommandRunNoSelection");

        Assert.False(host.Start(GroupOf()));
        Assert.Equal(["Status.CommandRunNoSelection"], reported);
    }

    [Fact]
    public async Task FailedRunReportsFailureKeyOnceAndHostRemainsUsable()
    {
        int failures = 0;
        List<string> reported = [];
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ScriptGroupHost host = new(
            () => true,
            (_, _) =>
            {
                entered.SetResult();
                throw new InvalidOperationException("send exploded");
            },
            statusReporter: reported.Add,
            errorLogger: _ => failures++);

        Assert.True(host.Start(GroupOf(MakeCommand("boom"))));
        await entered.Task;

        await host.StopAsync();

        Assert.Equal(1, failures);
        Assert.Contains("Status.CommandRunFailed", reported);
        Assert.True(host.Start(GroupOf(MakeCommand("ok"))));
        await host.StopAsync();
        Assert.Contains("Status.CommandRunFinished", reported);
    }

    [Fact]
    public async Task CommandStatusChanged_ForwardsRunnerTransitions()
    {
        ScriptCommand command = MakeCommand("A");
        List<ScriptCommandStatusEventArgs> observed = [];
        TaskCompletionSource sent = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ScriptGroupHost host = new(
            () => true,
            (_, _) =>
            {
                sent.TrySetResult();
                return Task.CompletedTask;
            });
        host.CommandStatusChanged += (_, args) => observed.Add(args);

        Assert.True(host.Start(GroupOf(command)));
        await sent.Task;
        await host.StopAsync();

        Assert.Contains(observed, item => item.CommandId == command.Id && item.State == ScriptCommandState.Sending);
        Assert.Contains(observed, item => item.CommandId == command.Id && item.State == ScriptCommandState.Delaying);
        await host.DisposeAsync();
    }

    [Fact]
    public async Task MultiTargetRunnerLosingAllTargetsReportsExplicitStatus()
    {
        List<string> reported = [];
        TaskCompletionSource completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        MultiTargetCommandScriptRunner runner = new(() => []);
        ScriptGroupHost host = new(() => true, runner, reported.Add);
        host.StateChanged += (_, _) =>
        {
            if (!host.IsRunning)
            {
                completed.TrySetResult();
            }
        };

        Assert.True(host.Start(GroupOf()));
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Contains("Status.CommandRunNoTargets", reported);
        await host.DisposeAsync();
    }
}
