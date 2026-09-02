using DuCom.Core.Sending;

namespace DuCom.Core.Tests.Sending;

public sealed class MultiTargetCommandScriptRunnerTests
{
    private static ScriptCommand Command(string payload, bool check = false, int timeout = 20) => new(
        Guid.NewGuid(), payload, 0, payload, false, 0, check, "OK", timeout, NewlinePolicy.None);

    [Fact]
    public async Task RunAsync_UsesStableTargetOrderAndRefreshesSelectionEachLoop()
    {
        List<string> sent = [];
        int loop = 0;
        using CancellationTokenSource cancellation = new();
        MultiTargetCommandScriptRunner runner = new(() =>
        {
            loop++;
            return loop == 1
                ? [Target("COM10"), Target("COM2")]
                : [Target("COM3")];
        });

        await runner.RunAsync(new CommandGroup(Guid.NewGuid(), "g", [Command("A")]), cancellation.Token);

        Assert.Equal(["COM10:A", "COM2:A", "COM3:A"], sent);

        ScriptCommandTarget Target(string name) => new(name, (command, _) =>
        {
            sent.Add($"{name}:{command.Payload}");
            if (sent.Count == 3)
            {
                cancellation.Cancel();
            }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task RunAsync_SendFailureIsVisibleAndDoesNotBlockOtherTargetOrNextCommand()
    {
        List<string> sent = [];
        List<ScriptCommandStatusEventArgs> states = [];
        using CancellationTokenSource cancellation = new();
        ScriptCommand second = Command("B");
        MultiTargetCommandScriptRunner runner = new(() =>
        [
            new("COM1", (command, _) => command.Payload == "A"
                ? Task.FromException(new IOException("broken"))
                : Record("COM1", command)),
            new("COM2", (command, _) => Record("COM2", command)),
        ]);
        runner.StatusChanged += (_, args) => states.Add(args);

        await runner.RunAsync(
            new CommandGroup(Guid.NewGuid(), "g", [Command("A"), second]),
            cancellation.Token);

        Assert.Contains("COM1:B", sent);
        Assert.Contains("COM2:A", sent);
        Assert.Contains(states, state => state.TargetName == "COM1" && state.State == ScriptCommandState.SendFailed && state.ErrorMessage == "broken");

        Task Record(string port, ScriptCommand command)
        {
            sent.Add($"{port}:{command.Payload}");
            if (command.Id == second.Id && sent.Contains("COM1:B") && sent.Contains("COM2:B"))
            {
                cancellation.Cancel();
            }
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task RunAsync_ResultChecksAreIndependentPerTarget()
    {
        List<ScriptCommandStatusEventArgs> states = [];
        using CancellationTokenSource cancellation = new();
        MultiTargetCommandScriptRunner runner = new(() =>
        [
            new("COM1", (_, _) => Task.CompletedTask, (_, _) => Task.FromResult(true)),
            new("COM2", (_, _) => Task.CompletedTask, (_, _) => Task.FromResult(false)),
        ]);
        runner.StatusChanged += (_, args) =>
        {
            states.Add(args);
            if (states.Any(state => state.TargetName == "COM1" && state.State == ScriptCommandState.ResultOk) &&
                states.Any(state => state.TargetName == "COM2" && state.State == ScriptCommandState.ResultTimeout))
            {
                cancellation.Cancel();
            }
        };

        await runner.RunAsync(new CommandGroup(Guid.NewGuid(), "g", [Command("AT", check: true)]), cancellation.Token);

        Assert.Contains(states, state => state.TargetName == "COM1" && state.State == ScriptCommandState.ResultOk);
        Assert.Contains(states, state => state.TargetName == "COM2" && state.State == ScriptCommandState.ResultTimeout);
    }

    [Fact]
    public async Task RunAsync_EmptyTargetSnapshotThrowsExplicitError()
    {
        MultiTargetCommandScriptRunner runner = new(() => []);

        await Assert.ThrowsAsync<CommandTargetsUnavailableException>(() =>
            runner.RunAsync(new CommandGroup(Guid.NewGuid(), "g", [Command("A")]), CancellationToken.None));
    }
}
