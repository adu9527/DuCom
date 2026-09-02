namespace DuCom.Core.Sending;

public enum ScriptCommandState
{
    Pending,
    Sending,
    ResultChecking,
    ResultOk,
    ResultTimeout,
    SendFailed,
    Delaying,
}

public sealed class ScriptCommandStatusEventArgs(
    Guid commandId,
    ScriptCommandState state,
    string? targetName = null,
    string? errorMessage = null) : EventArgs
{
    public Guid CommandId { get; } = commandId;

    public ScriptCommandState State { get; } = state;

    public string? TargetName { get; } = targetName;

    public string? ErrorMessage { get; } = errorMessage;
}

/// <summary>
/// Sequential loop executor for a <see cref="CommandGroup"/>: send each command in order,
/// optionally probe for an expected receive substring within a timeout, wait the configured
/// delay, and repeat the whole group until cancelled — mirroring the reference workflow of
/// continuous playback until an explicit stop.
/// </summary>
public sealed class CommandScriptRunner
{
    public const int ResultPollingIntervalMilliseconds = 100;

    private readonly Func<ScriptCommand, CancellationToken, Task> _send;
    private readonly Func<ScriptCommand, CancellationToken, Task<bool>>? _resultProbe;
    private Action<TimeSpan> _delay = static time => Thread.Sleep(time);

    public CommandScriptRunner(
        Func<ScriptCommand, CancellationToken, Task> send,
        Func<ScriptCommand, CancellationToken, Task<bool>>? resultProbe)
    {
        _send = send ?? throw new ArgumentNullException(nameof(send));
        _resultProbe = resultProbe;
    }

    /// <summary>Test seam that replaces the wall-clock delay implementation.</summary>
    internal CommandScriptRunner WithDelay(Action<TimeSpan> delay)
    {
        _delay = delay;
        return this;
    }

    public event EventHandler<ScriptCommandStatusEventArgs>? StatusChanged;

    /// <summary>
    /// Loops the group until the token fires. Returns when stopped; single pass completes first,
    /// so a stop request issued mid-group still lets commands already reached complete cleanly.
    /// </summary>
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
                foreach (ScriptCommand command in group.OrderedCommands())
                {
                    cancellation.ThrowIfCancellationRequested();
                    await ExecuteAsync(command, cancellation).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
    }

    private async Task ExecuteAsync(ScriptCommand command, CancellationToken cancellation)
    {
        Notify(command.Id, ScriptCommandState.Sending);
        await _send(command, cancellation).ConfigureAwait(false);

        if (command.IsResultCheck && !string.IsNullOrEmpty(command.ExpectedResult))
        {
            bool matched = await WaitForExpectedResultAsync(command, cancellation).ConfigureAwait(false);
            Notify(command.Id, matched ? ScriptCommandState.ResultOk : ScriptCommandState.ResultTimeout);
        }

        Notify(command.Id, ScriptCommandState.Delaying);
        if (command.DelayMilliseconds > 0)
        {
            TimeSpan remaining = TimeSpan.FromMilliseconds(command.DelayMilliseconds);
            while (remaining > TimeSpan.Zero && !cancellation.IsCancellationRequested)
            {
                TimeSpan slice = remaining > TimeSpan.FromMilliseconds(ResultPollingIntervalMilliseconds)
                    ? TimeSpan.FromMilliseconds(ResultPollingIntervalMilliseconds)
                    : remaining;
                await Task.Run(() => _delay(slice), cancellation).ConfigureAwait(false);
                remaining -= slice;
            }
        }
    }

    private async Task<bool> WaitForExpectedResultAsync(ScriptCommand command, CancellationToken cancellation)
    {
        if (_resultProbe is null)
        {
            return false;
        }

        Notify(command.Id, ScriptCommandState.ResultChecking);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        linked.CancelAfter(command.ResultTimeoutMilliseconds);

        try
        {
            while (!linked.IsCancellationRequested)
            {
                if (await _resultProbe(command, linked.Token).ConfigureAwait(false))
                {
                    return true;
                }

                await Task.Delay(Math.Min(ResultPollingIntervalMilliseconds, command.ResultTimeoutMilliseconds), linked.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            return false; // probe timed out
        }

        return false;
    }

    private void Notify(Guid id, ScriptCommandState state) =>
        StatusChanged?.Invoke(this, new ScriptCommandStatusEventArgs(id, state));
}
