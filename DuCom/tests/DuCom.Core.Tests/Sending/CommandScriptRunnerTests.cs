using DuCom.Core.Sending;
using Xunit;

namespace DuCom.Core.Tests.Sending;

public class CommandScriptRunnerTests
{
    private static ScriptCommand MakeCommand(
        string payload,
        int delayMs = 0,
        bool checkResult = false,
        int timeoutMs = 5_000)
    {
        ScriptCommand command = new(
            Guid.NewGuid(),
            payload,
            0,
            payload,
            false,
            delayMs,
            checkResult,
            "OK",
            timeoutMs,
            NewlinePolicy.None);
        return command;
    }

    /// <summary>Runner whose delay slices complete synchronously: fully deterministic.</summary>
    private static CommandScriptRunner CreateRunner(
        Func<ScriptCommand, CancellationToken, Task> send,
        Func<ScriptCommand, CancellationToken, Task<bool>>? resultProbe)
    {
        CommandScriptRunner runner = new(send, resultProbe);
        return runner.WithDelay(_ => { });
    }

    private static CommandGroup GroupOf(params ScriptCommand[] commands) =>
        new(Guid.NewGuid(), "g", commands);

    [Fact]
    public async Task RunAsync_CancelledBeforeStart_RunsNothing()
    {
        List<string> sent = [];
        CommandGroup group = GroupOf(MakeCommand("A"));
        CommandScriptRunner runner = CreateRunner((command, _) => { sent.Add(command.Payload); return Task.CompletedTask; }, null);

        using CancellationTokenSource cts = new();
        cts.Cancel();

        await runner.RunAsync(group, cts.Token);

        Assert.Empty(sent);
    }

    [Fact]
    public async Task RunAsync_StopMidGroup_LetsStartedPassComplete()
    {
        List<string> sent = [];
        ScriptCommand first = MakeCommand("A");
        ScriptCommand second = MakeCommand("B");
        CommandGroup group = GroupOf(first, second);

        using CancellationTokenSource cts = new();
        CommandScriptRunner runner = CreateRunner((command, _) =>
        {
            sent.Add(command.Payload);
            if (command.Payload == "B")
            {
                cts.Cancel();
            }

            return Task.CompletedTask;
        }, null);

        await runner.RunAsync(group, cts.Token);

        Assert.Equal(["A", "B"], sent);
    }

    [Fact]
    public async Task RunAsync_StatusTransitions_Reported()
    {
        List<ScriptCommandState> transitions = [];
        CommandGroup group = GroupOf(MakeCommand("CMD"));

        using CancellationTokenSource cts = new();
        CommandScriptRunner runner = CreateRunner((_, token) => { token.ThrowIfCancellationRequested(); cts.Cancel(); return Task.CompletedTask; }, null);
        runner.StatusChanged += (_, args) => transitions.Add(args.State);

        await runner.RunAsync(group, cts.Token);

        Assert.Contains(ScriptCommandState.Sending, transitions);
        Assert.Contains(ScriptCommandState.Delaying, transitions);
    }

    [Fact]
    public async Task RunAsync_ResultCheck_MatchingProbeReportsOkAndStops()
    {
        ScriptCommand command = MakeCommand("AT", checkResult: true, timeoutMs: 10_000);
        CommandGroup group = GroupOf(command);
        List<ScriptCommandState> states = [];
        int probes = 0;

        using CancellationTokenSource cts = new();
        CommandScriptRunner runner = CreateRunner((_, _) => Task.CompletedTask, (_, _) =>
        {
            probes++;
            return Task.FromResult(true); // matches on first poll
        });
        runner.StatusChanged += (_, args) =>
        {
            states.Add(args.State);
            if (args.State == ScriptCommandState.ResultOk)
            {
                cts.Cancel();
            }
        };

        await runner.RunAsync(group, cts.Token);

        Assert.Equal(1, probes);
        Assert.Contains(ScriptCommandState.ResultChecking, states);
        Assert.Contains(ScriptCommandState.ResultOk, states);
    }

    [Fact]
    public async Task RunAsync_ResultCheck_UnmatchedProbeReportsTimeout()
    {
        ScriptCommand command = MakeCommand("AT", checkResult: true, timeoutMs: 8);
        CommandGroup group = GroupOf(command);
        List<ScriptCommandState> states = [];

        using CancellationTokenSource cts = new();
        CommandScriptRunner runner = CreateRunner((_, _) => Task.CompletedTask, (_, _) => Task.FromResult(false));
        runner.StatusChanged += (_, args) =>
        {
            states.Add(args.State);
            if (args.State == ScriptCommandState.ResultTimeout)
            {
                cts.Cancel();
            }
        };

        await runner.RunAsync(group, cts.Token);

        Assert.Contains(ScriptCommandState.ResultTimeout, states);
    }

    [Fact]
    public async Task RunAsync_EmptyGroup_ReturnsImmediately()
    {
        using CancellationTokenSource cts = new();
        CommandScriptRunner runner = CreateRunner((_, _) => Task.CompletedTask, null);

        await runner.RunAsync(CommandGroup.Create("empty"), cts.Token);
    }
}
