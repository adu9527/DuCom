namespace DuCom.Core.Diagnostics;

public sealed class LoadMetrics
{
    private long _acceptedBlocks;
    private long _acceptedBytes;
    private long _evictions;
    private long _faults;
    private long _formattedLogBlocks;
    private long _lineRecords;
    private long _logQueuePeak;
    private long _producedBlocks;
    private long _producedBytes;
    private long _receiveQueuePeak;
    private long _shutdownDrainDurationTicks;
    private int _shutdownDrainState;
    private long _writtenLogBytes;
    private long _writtenLogRecords;

    public void AddProducedBlock(int byteCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(byteCount);
        Interlocked.Increment(ref _producedBlocks);
        Interlocked.Add(ref _producedBytes, byteCount);
    }

    public void AddAcceptedBlock(int byteCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(byteCount);
        Interlocked.Increment(ref _acceptedBlocks);
        Interlocked.Add(ref _acceptedBytes, byteCount);
    }

    public void AddFormattedLogBlock() => Interlocked.Increment(ref _formattedLogBlocks);

    public void AddWrittenLogRecord(int byteCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(byteCount);
        Interlocked.Increment(ref _writtenLogRecords);
        Interlocked.Add(ref _writtenLogBytes, byteCount);
    }

    public void AddLineRecords(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        Interlocked.Add(ref _lineRecords, count);
    }

    public void AddEvictions(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        Interlocked.Add(ref _evictions, count);
    }

    public void ObserveReceiveQueueDepth(int depth) => ObservePeak(ref _receiveQueuePeak, depth);

    public void ObserveLogQueueDepth(int depth) => ObservePeak(ref _logQueuePeak, depth);

    public void AddFault() => Interlocked.Increment(ref _faults);

    public void SetShutdownDrain(ShutdownDrainState state, TimeSpan duration)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(duration, TimeSpan.Zero);
        Interlocked.Exchange(ref _shutdownDrainDurationTicks, duration.Ticks);
        Volatile.Write(ref _shutdownDrainState, (int)state);
    }

    public PipelineMetricsSnapshot Snapshot() => new(
        Interlocked.Read(ref _producedBlocks),
        Interlocked.Read(ref _producedBytes),
        Interlocked.Read(ref _acceptedBlocks),
        Interlocked.Read(ref _acceptedBytes),
        Interlocked.Read(ref _formattedLogBlocks),
        Interlocked.Read(ref _writtenLogRecords),
        Interlocked.Read(ref _writtenLogBytes),
        Interlocked.Read(ref _lineRecords),
        Interlocked.Read(ref _evictions),
        Interlocked.Read(ref _receiveQueuePeak),
        Interlocked.Read(ref _logQueuePeak),
        Interlocked.Read(ref _faults),
        (ShutdownDrainState)Volatile.Read(ref _shutdownDrainState),
        TimeSpan.FromTicks(Interlocked.Read(ref _shutdownDrainDurationTicks)));

    private static void ObservePeak(ref long peak, int value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        long current = Interlocked.Read(ref peak);

        while (value > current)
        {
            long observed = Interlocked.CompareExchange(ref peak, value, current);
            if (observed == current)
            {
                return;
            }

            current = observed;
        }
    }
}
