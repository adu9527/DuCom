using System.Diagnostics;

namespace DuCom.Services;

internal sealed class SerialWarningAggregator : IDisposable
{
    internal static readonly TimeSpan NormalInterval = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan HighMemoryInterval = TimeSpan.FromSeconds(5);

    private readonly object _syncRoot = new();
    private readonly Dictionary<string, long> _counts = new(StringComparer.Ordinal);
    private readonly Func<long> _memoryThresholdBytes;
    private readonly Action<string, long, bool> _publish;
    private readonly Process _process = Process.GetCurrentProcess();
    private readonly Timer _timer;
    private long _batchStartedTimestamp;
    private bool _disposed;

    public SerialWarningAggregator(
        Func<long> memoryThresholdBytes,
        Action<string, long, bool> publish)
    {
        _memoryThresholdBytes = memoryThresholdBytes ?? throw new ArgumentNullException(nameof(memoryThresholdBytes));
        _publish = publish ?? throw new ArgumentNullException(nameof(publish));
        _timer = new Timer(OnTimer, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public void Report(string warning)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(warning);

        lock (_syncRoot)
        {
            if (_disposed)
            {
                return;
            }

            if (_counts.Count == 0)
            {
                _batchStartedTimestamp = Stopwatch.GetTimestamp();
                _timer.Change(NormalInterval, Timeout.InfiniteTimeSpan);
            }

            _counts.TryGetValue(warning, out long count);
            _counts[warning] = count == long.MaxValue ? long.MaxValue : count + 1;
        }
    }

    public void Dispose()
    {
        List<KeyValuePair<string, long>> pending;
        bool highMemory;
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            highMemory = IsHighMemory();
            pending = DrainCounts();
        }

        _timer.Dispose();
        _process.Dispose();
        Publish(pending, highMemory);
    }

    private void OnTimer(object? state)
    {
        List<KeyValuePair<string, long>> pending;
        bool highMemory;
        lock (_syncRoot)
        {
            if (_disposed || _counts.Count == 0)
            {
                return;
            }

            highMemory = IsHighMemory();
            TimeSpan interval = highMemory ? HighMemoryInterval : NormalInterval;
            TimeSpan elapsed = Stopwatch.GetElapsedTime(_batchStartedTimestamp);
            if (elapsed < interval)
            {
                _timer.Change(interval - elapsed, Timeout.InfiniteTimeSpan);
                return;
            }

            pending = DrainCounts();
        }

        Publish(pending, highMemory);
    }

    private bool IsHighMemory()
    {
        try
        {
            _process.Refresh();
            return _process.PrivateMemorySize64 >= Math.Max(1, _memoryThresholdBytes());
        }
        catch
        {
            return false;
        }
    }

    private List<KeyValuePair<string, long>> DrainCounts()
    {
        List<KeyValuePair<string, long>> pending = [.. _counts];
        _counts.Clear();
        _batchStartedTimestamp = 0;
        return pending;
    }

    private void Publish(IEnumerable<KeyValuePair<string, long>> pending, bool highMemory)
    {
        foreach ((string warning, long count) in pending)
        {
            try
            {
                _publish(warning, count, highMemory);
            }
            catch
            {
                // A warning must never interrupt serial receive processing.
            }
        }
    }
}
