using System.Diagnostics;

namespace DuCom.Core.Diagnostics;

/// <summary>
/// Shared periodic background worker used by the watchdog, variable monitor, and Telnet
/// bridge push loop. Guarantees: strictly sequential ticks (the next tick never starts
/// while the previous one is still running — overlapping due periods are absorbed by the
/// periodic timer, so a slow tick delays later ticks instead of piling up), every tick
/// exception is isolated to the diagnostic log instead of faulting the loop, and disposal
/// cancels and waits for the in-flight tick before returning. Ticks are never started
/// after disposal.
/// </summary>
public sealed class PeriodicBackgroundWorker : IAsyncDisposable
{
    private readonly string _name;
    private readonly TimeSpan _interval;
    private readonly Func<CancellationToken, Task> _tickAsync;
    private readonly Action<string, Exception>? _log;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly object _startGate = new();
    private Task? _loopTask;
    private Task? _disposeTask;
    private int _tickExecutions;
    private int _started;
    private int _disposed;

    public PeriodicBackgroundWorker(
        string name,
        TimeSpan interval,
        Func<CancellationToken, Task> tickAsync,
        Action<string, Exception>? log = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
        _name = name;
        _interval = interval;
        _tickAsync = tickAsync ?? throw new ArgumentNullException(nameof(tickAsync));
        _log = log;
    }

    public int TickExecutions => Volatile.Read(ref _tickExecutions);

    /// <summary>True while a tick is currently running (never concurrent with another tick).</summary>
    public bool IsTickInProgress => Volatile.Read(ref _tickInProgress) != 0;

    private int _tickInProgress;

    /// <summary>
    /// Starts the loop. Optional <paramref name="firstDelay"/> delays the first tick.
    /// Throws <see cref="ObjectDisposedException"/> after disposal — a loop started during
    /// or after disposal could never be awaited by it. Start and Dispose serialize on one
    /// gate: whichever acquires it first wins, so a Start that loses to Dispose throws
    /// instead of leaking an unobserved loop task (2026-08-28 review).
    /// </summary>
    public void Start(TimeSpan? firstDelay = null)
    {
        lock (_startGate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (Volatile.Read(ref _started) != 0)
            {
                return;
            }

            Volatile.Write(ref _started, 1);
            _loopTask = Task.Run(() => LoopAsync(firstDelay));
        }
    }

    private async Task LoopAsync(TimeSpan? firstDelay)
    {
        try
        {
            if (firstDelay is { } delay)
            {
                await Task.Delay(delay, _cancellation.Token).ConfigureAwait(false);
            }

            using PeriodicTimer timer = new(_interval);
            while (!_cancellation.IsCancellationRequested)
            {
                if (!await timer.WaitForNextTickAsync(_cancellation.Token).ConfigureAwait(false))
                {
                    break;
                }

                Volatile.Write(ref _tickInProgress, 1);
                try
                {
                    Interlocked.Increment(ref _tickExecutions);
                    await _tickAsync(_cancellation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    _log?.Invoke(_name, exception);
                }
                finally
                {
                    Volatile.Write(ref _tickInProgress, 0);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            // The loop itself must never die silently on an unexpected fault.
            _log?.Invoke(_name, exception);
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_startGate)
        {
            _disposeTask ??= DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        Volatile.Write(ref _disposed, 1);
        _cancellation.Cancel();
        Task? loop = _loopTask; // captured under _startGate via the DisposeAsync entry
        if (loop is not null)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch
            {
                // Loop faults are already logged; disposal must not throw.
            }
        }

        _cancellation.Dispose();
    }
}
