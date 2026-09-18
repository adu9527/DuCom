using System.Diagnostics;

namespace DuCom.Core.Sessions;

public enum RawTrafficDirection
{
    Rx,
    Tx,
}

public sealed record RawTrafficCursor(
    Guid Generation,
    long Sequence,
    long RxByteOffset,
    long TxByteOffset);

public sealed record RawTrafficRecord(
    RawTrafficCursor Cursor,
    RawTrafficDirection Direction,
    long ByteOffset,
    long MonotonicTimestamp,
    DateTimeOffset TimestampUtc,
    long SettingsRevision,
    ReadOnlyMemory<byte> Bytes);

public sealed record RawTrafficGap(
    RawTrafficCursor Cursor,
    long DroppedBlocks,
    long DroppedBytes,
    string Reason);

public sealed record RawTrafficFault(string SubscriptionId, string Message);

public sealed record RawTrafficSnapshot(
    IReadOnlyList<RawTrafficRecord> Records,
    RawTrafficGap? Gap,
    RawTrafficCursor? Cursor,
    long TotalDroppedBlocks,
    long TotalDroppedBytes,
    RawTrafficFault? Fault,
    bool IsDisposed);

public sealed record RawTrafficSubscriptionOptions
{
    public required string Id { get; init; }

    public int MaximumBlocks { get; init; } = 256;

    public int MaximumBytes { get; init; } = 1024 * 1024;

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Id);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumBlocks);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumBytes);
    }
}

/// <summary>
/// One legacy raw receive observer. <see cref="Publish"/> runs on the receive pipeline thread:
/// it must copy the span immediately, never block, and never touch UI objects.
/// This compatibility API remains RX-only.
/// </summary>
public sealed class SessionRawTap
{
    public required string Id { get; init; }

    public required Action<ReadOnlyMemory<byte>, DateTimeOffset> Publish { get; init; }
}

public sealed class RawTrafficSubscription : IAsyncDisposable, IDisposable
{
    private readonly SessionRawTapHub _hub;
    private readonly object _gate = new();
    private readonly Queue<RawTrafficRecord> _records = new();
    private readonly int _maximumBlocks;
    private readonly int _maximumBytes;
    private TaskCompletionSource _available = CreateSignal();
    private int _queuedBytes;
    private long _totalDroppedBlocks;
    private long _totalDroppedBytes;
    private long _pendingDroppedBlocks;
    private long _pendingDroppedBytes;
    private string? _pendingGapReason;
    private RawTrafficCursor? _gapCursor;
    private RawTrafficCursor? _cursor;
    private RawTrafficFault? _fault;
    private bool _disposed;

    internal RawTrafficSubscription(SessionRawTapHub hub, RawTrafficSubscriptionOptions options)
    {
        _hub = hub;
        Id = options.Id;
        _maximumBlocks = options.MaximumBlocks;
        _maximumBytes = options.MaximumBytes;
    }

    public string Id { get; }

    public RawTrafficSnapshot Snapshot()
    {
        lock (_gate)
        {
            return DrainSnapshot();
        }
    }

    public async ValueTask<RawTrafficSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Task signal;
            lock (_gate)
            {
                if (_records.Count > 0 || _pendingGapReason is not null || _fault is not null || _disposed)
                {
                    return DrainSnapshot();
                }

                signal = _available.Task;
            }

            await signal.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        bool unregister;
        lock (_gate)
        {
            unregister = !_disposed;
            _disposed = true;
            _available.TrySetResult();
        }

        if (unregister)
        {
            _hub.Unsubscribe(this);
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    internal void Enqueue(RawTrafficRecord record)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            int recordBytes = record.Bytes.Length;
            while (_records.Count > 0 && (_records.Count >= _maximumBlocks || _queuedBytes + recordBytes > _maximumBytes))
            {
                RawTrafficRecord dropped = _records.Dequeue();
                _queuedBytes -= dropped.Bytes.Length;
                AddGap(dropped.Cursor, 1, dropped.Bytes.Length, "QueueOverflow");
            }

            if (recordBytes > _maximumBytes)
            {
                AddGap(record.Cursor, 1, recordBytes, "QueueOverflow");
            }
            else
            {
                _records.Enqueue(record);
                _queuedBytes += recordBytes;
                _cursor = record.Cursor;
            }

            _available.TrySetResult();
        }
    }

    internal void Flush(RawTrafficCursor cursor, string reason)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _gapCursor = cursor;
            _pendingGapReason = reason;
            _cursor = cursor;
            _available.TrySetResult();
        }
    }

    internal void SetFault(Exception exception)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _fault = new RawTrafficFault(Id, exception.ToString());
            _available.TrySetResult();
        }
    }

    internal void TrimForMemoryPressure(int maximumBlocks = 32, int maximumBytes = 256 * 1024)
    {
        lock (_gate)
        {
            while (_records.Count > maximumBlocks || _queuedBytes > maximumBytes)
            {
                RawTrafficRecord dropped = _records.Dequeue();
                _queuedBytes -= dropped.Bytes.Length;
                AddGap(dropped.Cursor, 1, dropped.Bytes.Length, "MemoryPressure");
            }
            _available.TrySetResult();
        }
    }

    private void AddGap(RawTrafficCursor cursor, long blocks, long bytes, string reason)
    {
        _gapCursor = cursor;
        _pendingDroppedBlocks += blocks;
        _pendingDroppedBytes += bytes;
        _totalDroppedBlocks += blocks;
        _totalDroppedBytes += bytes;
        _pendingGapReason = reason;
        _cursor = cursor;
    }

    private RawTrafficSnapshot DrainSnapshot()
    {
        RawTrafficRecord[] records = [.. _records];
        _records.Clear();
        _queuedBytes = 0;
        RawTrafficGap? gap = _pendingGapReason is null || _gapCursor is null
            ? null
            : new RawTrafficGap(_gapCursor, _pendingDroppedBlocks, _pendingDroppedBytes, _pendingGapReason);
        _pendingDroppedBlocks = 0;
        _pendingDroppedBytes = 0;
        _pendingGapReason = null;
        _gapCursor = null;
        RawTrafficFault? fault = _fault;
        _fault = null;
        _available = CreateSignal();
        return new RawTrafficSnapshot(records, gap, _cursor, _totalDroppedBlocks, _totalDroppedBytes, fault, _disposed);
    }

    private static TaskCompletionSource CreateSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>Per-session raw RX/TX traffic fan-out with an RX-only legacy tap surface.</summary>
public sealed class SessionRawTapHub
{
    private const int MaximumTapCount = 4;
    private readonly object _gate = new();
    private readonly Dictionary<string, SessionRawTap> _taps = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RawTrafficSubscription> _subscriptions = new(StringComparer.Ordinal);
    private Guid _generation;
    private long _sequence;
    private long _rxByteOffset;
    private long _txByteOffset;
    private long _settingsRevision;
    private bool _generationActive;

    public event EventHandler<RawTrafficFault>? Faulted;

    public Guid? Generation
    {
        get
        {
            lock (_gate)
            {
                return _generationActive ? _generation : null;
            }
        }
    }

    public IDisposable Register(SessionRawTap tap)
    {
        ArgumentNullException.ThrowIfNull(tap);
        lock (_gate)
        {
            if (!_taps.ContainsKey(tap.Id) && _taps.Count >= MaximumTapCount)
            {
                throw new InvalidOperationException($"At most {MaximumTapCount} raw taps may be registered per session.");
            }

            _taps[tap.Id] = tap;
        }

        return new Registration(this, tap.Id);
    }

    public RawTrafficSubscription Subscribe(RawTrafficSubscriptionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        RawTrafficSubscription subscription = new(this, options);
        lock (_gate)
        {
            if (_subscriptions.ContainsKey(options.Id))
            {
                throw new InvalidOperationException($"Raw traffic subscription '{options.Id}' is already registered.");
            }

            _subscriptions.Add(options.Id, subscription);
        }

        return subscription;
    }

    public Guid BeginGeneration(long settingsRevision)
    {
        RawTrafficSubscription[] subscriptions;
        RawTrafficCursor? previous = null;
        lock (_gate)
        {
            subscriptions = [.. _subscriptions.Values];
            if (_generationActive)
            {
                previous = CurrentCursor();
            }

            _generation = Guid.NewGuid();
            _sequence = 0;
            _rxByteOffset = 0;
            _txByteOffset = 0;
            _settingsRevision = settingsRevision;
            _generationActive = true;
        }

        if (previous is not null)
        {
            foreach (RawTrafficSubscription subscription in subscriptions)
            {
                subscription.Flush(previous, "Reopened");
            }
        }

        return _generation;
    }

    public void UpdateSettingsRevision(long settingsRevision)
    {
        lock (_gate)
        {
            _settingsRevision = settingsRevision;
        }
    }

    public void EndGeneration(string reason)
    {
        RawTrafficSubscription[] subscriptions;
        RawTrafficCursor cursor;
        lock (_gate)
        {
            if (!_generationActive)
            {
                return;
            }

            cursor = CurrentCursor();
            _generationActive = false;
            subscriptions = [.. _subscriptions.Values];
        }

        foreach (RawTrafficSubscription subscription in subscriptions)
        {
            subscription.Flush(cursor, reason);
        }
    }

    public void TrimForMemoryPressure()
    {
        RawTrafficSubscription[] subscriptions;
        lock (_gate)
        {
            subscriptions = [.. _subscriptions.Values];
        }
        foreach (RawTrafficSubscription subscription in subscriptions)
        {
            subscription.TrimForMemoryPressure();
        }
    }

    // Legacy compatibility: RX-only, callback memory is valid only for the callback.
    public void PublishRaw(ReadOnlyMemory<byte> bytes, DateTimeOffset receivedAtUtc) =>
        PublishReceive(bytes, receivedAtUtc);

    public void PublishReceive(ReadOnlyMemory<byte> bytes, DateTimeOffset receivedAtUtc)
    {
        if (bytes.IsEmpty)
        {
            return;
        }

        PublishOwned(RawTrafficDirection.Rx, bytes, receivedAtUtc);

        SessionRawTap[] taps;
        lock (_gate)
        {
            taps = [.. _taps.Values];
        }

        foreach (SessionRawTap tap in taps)
        {
            try
            {
                tap.Publish(bytes, receivedAtUtc);
            }
            catch (Exception exception)
            {
                lock (_gate)
                {
                    _taps.Remove(tap.Id);
                }

                Faulted?.Invoke(this, new RawTrafficFault(tap.Id, exception.ToString()));
            }
        }

    }

    public void PublishTransmit(ReadOnlyMemory<byte> bytes, DateTimeOffset sentAtUtc)
    {
        if (!bytes.IsEmpty)
        {
            PublishOwned(RawTrafficDirection.Tx, bytes, sentAtUtc);
        }
    }

    public void ReportSubscriptionFault(string subscriptionId, Exception exception)
    {
        RawTrafficSubscription? subscription;
        lock (_gate)
        {
            _subscriptions.TryGetValue(subscriptionId, out subscription);
        }

        subscription?.SetFault(exception);
        Faulted?.Invoke(this, new RawTrafficFault(subscriptionId, exception.ToString()));
    }

    internal void Unsubscribe(RawTrafficSubscription subscription)
    {
        lock (_gate)
        {
            if (_subscriptions.TryGetValue(subscription.Id, out RawTrafficSubscription? current) && ReferenceEquals(current, subscription))
            {
                _subscriptions.Remove(subscription.Id);
            }
        }
    }

    private void PublishOwned(RawTrafficDirection direction, ReadOnlyMemory<byte> bytes, DateTimeOffset timestampUtc)
    {
        RawTrafficSubscription[] subscriptions;
        RawTrafficCursor cursor;
        long byteOffset;
        long monotonicTimestamp;
        long settingsRevision;
        lock (_gate)
        {
            if (!_generationActive)
            {
                return;
            }

            byteOffset = direction == RawTrafficDirection.Rx ? _rxByteOffset : _txByteOffset;
            if (direction == RawTrafficDirection.Rx)
            {
                _rxByteOffset += bytes.Length;
            }
            else
            {
                _txByteOffset += bytes.Length;
            }

            cursor = new RawTrafficCursor(_generation, ++_sequence, _rxByteOffset, _txByteOffset);
            monotonicTimestamp = Stopwatch.GetTimestamp();
            settingsRevision = _settingsRevision;
            subscriptions = [.. _subscriptions.Values];
        }

        foreach (RawTrafficSubscription subscription in subscriptions)
        {
            subscription.Enqueue(new RawTrafficRecord(
                cursor,
                direction,
                byteOffset,
                monotonicTimestamp,
                timestampUtc,
                settingsRevision,
                bytes.ToArray()));
        }
    }

    private RawTrafficCursor CurrentCursor() => new(_generation, _sequence, _rxByteOffset, _txByteOffset);

    private sealed class Registration(SessionRawTapHub hub, string id) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            lock (hub._gate)
            {
                hub._taps.Remove(id);
            }
        }
    }
}
