namespace DuCom.Core.Sending;

/// <summary>
/// Lifecycle owner for one advanced-command-group runner instance: exactly one active run,
/// explicit stop, idempotent disposal that cancels and awaits an in-flight run, and a state-
/// changed notification. Input admission, transport access, and diagnostics are injected, so
/// UI layers stay free of concurrency details. UI windows may observe <see cref="StateChanged"/>
/// but must never dispose a host they did not create.
/// </summary>
public sealed class ScriptGroupHost : IAsyncDisposable
{
    private readonly Func<bool> _canStart;
    private readonly Func<CommandGroup, CancellationToken, Task> _run;
    private readonly Action<string>? _statusReporter;
    private readonly Action<Exception>? _errorLogger;
    private readonly string _startRejectedStatusKey;
    private readonly object _gate = new();
    private CancellationTokenSource? _runCancellation;
    private Task _runTask = Task.CompletedTask;
    private CommandGroup? _runningGroup;
    private int _disposed;

    public ScriptGroupHost(
        Func<bool> canStart,
        Func<ScriptCommand, CancellationToken, Task> send,
        Func<ScriptCommand, CancellationToken, Task<bool>>? resultProbe = null,
        Action<string>? statusReporter = null,
        Action<Exception>? errorLogger = null,
        string startRejectedStatusKey = "Status.CommandRunNoSession")
    {
        _canStart = canStart ?? throw new ArgumentNullException(nameof(canStart));
        ArgumentNullException.ThrowIfNull(send);
        _statusReporter = statusReporter;
        _errorLogger = errorLogger;
        _startRejectedStatusKey = startRejectedStatusKey;
        CommandScriptRunner runner = new(send, resultProbe);
        runner.StatusChanged += OnCommandStatusChanged;
        _run = runner.RunAsync;
    }

    public ScriptGroupHost(
        Func<bool> canStart,
        MultiTargetCommandScriptRunner runner,
        Action<string>? statusReporter = null,
        Action<Exception>? errorLogger = null,
        string startRejectedStatusKey = "Status.CommandRunNoSession")
    {
        _canStart = canStart ?? throw new ArgumentNullException(nameof(canStart));
        ArgumentNullException.ThrowIfNull(runner);
        _statusReporter = statusReporter;
        _errorLogger = errorLogger;
        _startRejectedStatusKey = startRejectedStatusKey;
        runner.StatusChanged += OnCommandStatusChanged;
        _run = runner.RunAsync;
    }

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _runningGroup is not null && !_runTask.IsCompleted;
            }
        }
    }

    public CommandGroup? RunningGroup
    {
        get
        {
            lock (_gate)
            {
                return _runningGroup;
            }
        }
    }

    /// <summary>Raised whenever IsRunning flips. Handlers must be cheap.</summary>
    public event EventHandler? StateChanged;

    /// <summary>Forwards the active runner's per-command state transitions.</summary>
    public event EventHandler<ScriptCommandStatusEventArgs>? CommandStatusChanged;

    /// <summary>
    /// Starts looping the group. Returns false without side effects when disposed, when a run
    /// is already active, or when <paramref name="group"/> cannot start (reported via the
    /// status reporter).
    /// </summary>
    public bool Start(CommandGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (IsRunning)
        {
            return false;
        }

        if (!_canStart())
        {
            _statusReporter?.Invoke(_startRejectedStatusKey);
            return false;
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            _runCancellation = new CancellationTokenSource();
            _runningGroup = group;
            _runTask = Task.Run(async () =>
            {
                CancellationTokenSource? captured;
                lock (_gate)
                {
                    captured = _runCancellation;
                }

                try
                {
                    if (captured is not null)
                    {
                        await _run(group, captured.Token).ConfigureAwait(false);
                    }

                    _statusReporter?.Invoke("Status.CommandRunFinished");
                }
                catch (CommandTargetsUnavailableException exception)
                {
                    _errorLogger?.Invoke(exception);
                    _statusReporter?.Invoke("Status.CommandRunNoTargets");
                }
                catch (Exception exception)
                {
                    _errorLogger?.Invoke(exception);
                    _statusReporter?.Invoke("Status.CommandRunFailed");
                }
                finally
                {
                    CompleteRun();
                }
            }, CancellationToken.None);
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Cancels any active run and waits for it to drain. Completes immediately otherwise.</summary>
    public async Task StopAsync()
    {
        CancellationTokenSource? cancellation;
        Task run;
        lock (_gate)
        {
            cancellation = _runCancellation;
            run = _runTask;
        }

        if (run.IsCompleted)
        {
            return;
        }

        cancellation?.Cancel();
        try
        {
            await run.ConfigureAwait(false);
        }
        catch
        {
            // The run wrapper reports its own failures; stopping never rethrows.
        }
    }

    /// <summary>Idempotent: the first call stops the active run; later calls complete immediately.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await StopAsync().ConfigureAwait(false);
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            cancellation = _runCancellation;
        }

        cancellation?.Dispose();
        GC.SuppressFinalize(this);
    }

    private void CompleteRun()
    {
        lock (_gate)
        {
            _runCancellation = null;
            _runningGroup = null;
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnCommandStatusChanged(object? sender, ScriptCommandStatusEventArgs e) =>
        CommandStatusChanged?.Invoke(this, e);
}
