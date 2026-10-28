namespace DuCom.Services;

/// <summary>Bounds teardown even if the dispatcher or a native serial driver is blocked.</summary>
internal sealed class ShutdownWatchdog
{
    private readonly object _gate = new();
    private bool _completed;
    private readonly TimeSpan _timeout;
    private readonly Action _onTimeout;
    private int _started;

    internal ShutdownWatchdog(TimeSpan timeout, Action onTimeout)
    {
        _timeout = timeout;
        _onTimeout = onTimeout;
    }

    internal void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        // A dedicated thread also works when blocked cleanup exhausts the thread pool.
        new Thread(() =>
        {
            lock (_gate)
            {
                if (_completed) return;
                long started = System.Diagnostics.Stopwatch.GetTimestamp();
                TimeSpan remaining = _timeout;
                while (!_completed && remaining > TimeSpan.Zero)
                {
                    Monitor.Wait(_gate, remaining);
                    remaining = _timeout - System.Diagnostics.Stopwatch.GetElapsedTime(started);
                }
                if (_completed) return;
            }
            _onTimeout();
        }) { IsBackground = true, Name = "DuCom shutdown watchdog" }.Start();
    }

    internal void Complete()
    {
        lock (_gate)
        {
            _completed = true;
            Monitor.PulseAll(_gate);
        }
    }
}
