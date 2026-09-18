using System.Collections.ObjectModel;

namespace DuCom.Core.Protocols;

public readonly record struct ProtocolFrameCursor(long StoreGeneration, long StoreSequence);

public sealed record StoredProtocolFrame(long StoreSequence, ProtocolFrame Frame);

public sealed record ProtocolFrameSnapshot(
    long StoreGeneration,
    long? FirstSequence,
    long? LastSequence,
    long EvictedCount,
    bool CursorReset,
    IReadOnlyList<StoredProtocolFrame> Frames);

public sealed class ProtocolFrameStore
{
    private readonly object _gate = new();
    private readonly int _capacity;
    private readonly Queue<StoredProtocolFrame> _frames;
    private long _generation = 1;
    private long _nextSequence = 1;
    private long _evictedCount;

    public ProtocolFrameStore(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _capacity = capacity;
        _frames = new Queue<StoredProtocolFrame>(Math.Min(capacity, 4_096));
    }

    public ProtocolFrameCursor Append(ProtocolFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        lock (_gate)
        {
            long sequence = _nextSequence++;
            _frames.Enqueue(new StoredProtocolFrame(sequence, frame));
            if (_frames.Count > _capacity)
            {
                _frames.Dequeue();
                _evictedCount++;
            }

            return new ProtocolFrameCursor(_generation, sequence);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _frames.Clear();
            _generation++;
            _nextSequence = 1;
            _evictedCount = 0;
        }
    }

    public ProtocolFrameSnapshot Snapshot() => SnapshotAfter(null, int.MaxValue);

    public ProtocolFrameSnapshot SnapshotAfter(ProtocolFrameCursor? cursor, int maximumFrames)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumFrames);
        lock (_gate)
        {
            bool reset = cursor.HasValue && cursor.Value.StoreGeneration != _generation;
            long after = !reset && cursor.HasValue ? cursor.Value.StoreSequence : 0;
            StoredProtocolFrame[] selected = _frames
                .Where(item => item.StoreSequence > after)
                .Take(maximumFrames)
                .ToArray();
            // Only the oldest frames are evicted, so a nonempty queue ends at the last assigned sequence.
            return new ProtocolFrameSnapshot(
                _generation,
                _frames.Count == 0 ? null : _frames.Peek().StoreSequence,
                _frames.Count == 0 ? null : _nextSequence - 1,
                _evictedCount,
                reset,
                new ReadOnlyCollection<StoredProtocolFrame>(selected));
        }
    }
}
