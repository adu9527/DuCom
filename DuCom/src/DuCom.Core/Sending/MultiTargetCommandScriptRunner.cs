namespace DuCom.Core.Sending;

public sealed record ScriptCommandTarget(
    string Name,
    Func<ScriptCommand, CancellationToken, Task> Send,
    Func<ScriptCommand, CancellationToken, Task<bool>>? ResultProbe = null);

public sealed class CommandTargetsUnavailableException(string message) : InvalidOperationException(message);

/// <summary>
/// Runs one command-group loop independently on every selected target. The target snapshot is
/// refreshed between loops, ordered by name, and processed concurrently so a slow or failing
/// target cannot hold up another target.
/// </summary>
public sealed class MultiTargetCommandScriptRunner
{
    private readonly Func<IReadOnlyList<ScriptCommandTarget>> _targetProvider;
    private Func<TimeSpan, CancellationToken, Task> _delay = Task.Delay;

    public MultiTargetCommandScriptRunner(Func<IReadOnlyList<ScriptCommandTarget>> targetProvider)
    {
        _targetProvider = targetProvider ?? throw new ArgumentNullException(nameof(targetProvider));
    }

    internal MultiTargetCommandScriptRunner WithDelay(Func<TimeSpan, CancellationToken, Task> delay)
    {
        _delay = delay;
        return this;
    }

    public event EventHandler<ScriptCommandStatusEventArgs>? StatusChanged;

    public async Task RunAsync(CommandGroup group, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (group.Commands.Count == 0)
        {
            return;
        }

        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                ScriptCommandTarget[] targets = [.. _targetProvider()
                    .OrderBy(target => target.Name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(target => target.Name, StringComparer.Ordinal)];
                if (targets.Length == 0)
                {
                    throw new CommandTargetsUnavailableException("No selected command target is open.");
                }

                Task[] targetRuns = targets.Select(target => RunLoopForTargetAsync(group, target, cancellation)).ToArray();
                await Task.WhenAll(targetRuns).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
    }

    private async Task RunLoopForTargetAsync(
        CommandGroup group,
        ScriptCommandTarget target,
        CancellationToken cancellation)
    {
        foreach (ScriptCommand command in group.OrderedCommands())
        {
            cancellation.ThrowIfCancellationRequested();
            Notify(command.Id, ScriptCommandState.Sending, target.Name);
            try
            {
                await target.Send(command, cancellation).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                Notify(command.Id, ScriptCommandState.SendFailed, target.Name, exception.Message);
                continue;
            }

            if (command.IsResultCheck && !string.IsNullOrEmpty(command.ExpectedResult))
            {
                bool matched = await WaitForExpectedResultAsync(command, target, cancellation).ConfigureAwait(false);
                Notify(
                    command.Id,
                    matched ? ScriptCommandState.ResultOk : ScriptCommandState.ResultTimeout,
                    target.Name);
            }

            Notify(command.Id, ScriptCommandState.Delaying, target.Name);
            if (command.DelayMilliseconds > 0)
            {
                await _delay(TimeSpan.FromMilliseconds(command.DelayMilliseconds), cancellation).ConfigureAwait(false);
            }
        }
    }

    private async Task<bool> WaitForExpectedResultAsync(
        ScriptCommand command,
        ScriptCommandTarget target,
        CancellationToken cancellation)
    {
        if (target.ResultProbe is null)
        {
            return false;
        }

        Notify(command.Id, ScriptCommandState.ResultChecking, target.Name);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        linked.CancelAfter(command.ResultTimeoutMilliseconds);
        try
        {
            while (!linked.IsCancellationRequested)
            {
                if (await target.ResultProbe(command, linked.Token).ConfigureAwait(false))
                {
                    return true;
                }

                await Task.Delay(
                    Math.Min(CommandScriptRunner.ResultPollingIntervalMilliseconds, command.ResultTimeoutMilliseconds),
                    linked.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            return false;
        }

        return false;
    }

    private void Notify(Guid commandId, ScriptCommandState state, string targetName, string? errorMessage = null) =>
        StatusChanged?.Invoke(this, new ScriptCommandStatusEventArgs(commandId, state, targetName, errorMessage));
}
