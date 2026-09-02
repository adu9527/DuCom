using System.Collections.ObjectModel;
using System.Text;

namespace DuCom.Core.Storage;

public sealed class BudgetedLineStore
{
    private readonly object _gate = new();
    private readonly int _maxTextBytes;
    private readonly int _maxSegmentCharacters;
    private readonly List<LogicalLine> _logicalLines = [];
    private long _evictedLineCount;
    private long _nextLogicalId = 1;
    private int _textBytes;

    public BudgetedLineStore(int maxTextBytes, int maxSegmentCharacters)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxTextBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxSegmentCharacters);

        _maxTextBytes = maxTextBytes;
        _maxSegmentCharacters = maxSegmentCharacters;
    }

    public long Append(LineDirection direction, DateTimeOffset timestamp, string text, bool isTerminated)
    {
        ArgumentNullException.ThrowIfNull(text);

        lock (_gate)
        {
            long logicalId = _nextLogicalId++;
            LogicalLine logicalLine = CreateLogicalLine(logicalId, direction, timestamp, text, isTerminated);
            _logicalLines.Add(logicalLine);
            _textBytes += logicalLine.TextBytes;

            while (_textBytes > _maxTextBytes)
            {
                LogicalLine evicted = _logicalLines[0];
                _logicalLines.RemoveAt(0);
                _textBytes -= evicted.TextBytes;
                _evictedLineCount++;
            }

            return logicalId;
        }
    }

    public void AppendContinuation(long logicalId, string text, bool isTerminated)
    {
        ArgumentNullException.ThrowIfNull(text);
        lock (_gate)
        {
            LogicalLine? existing = FindLogicalLine(logicalId);
            if (existing is null)
            {
                return;
            }

            LogicalLine continuation = CreateLogicalLine(logicalId, existing.Segments[0].Direction, existing.Segments[0].TimestampUtc, text, isTerminated);
            int firstSegmentIndex = existing.Segments.Count;
            for (int index = 0; index < continuation.Segments.Count; index++)
            {
                existing.Segments.Add(continuation.Segments[index] with
                {
                    SegmentIndex = firstSegmentIndex + index,
                });
            }

            existing.TextBytes += continuation.TextBytes;
            _textBytes += continuation.TextBytes;
            while (_textBytes > _maxTextBytes && _logicalLines.Count > 0)
            {
                LogicalLine evicted = _logicalLines[0];
                _logicalLines.RemoveAt(0);
                _textBytes -= evicted.TextBytes;
                _evictedLineCount++;
            }
        }
    }

    public void CompleteContinuation(long logicalId)
    {
        lock (_gate)
        {
            LogicalLine? existing = FindLogicalLine(logicalId);
            if (existing is null || existing.Segments.Count == 0)
            {
                return;
            }

            existing.Segments[^1] = existing.Segments[^1] with { IsTerminated = true };
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _evictedLineCount += _logicalLines.Count;
            _logicalLines.Clear();
            _textBytes = 0;
        }
    }

    public LineStoreSnapshot Snapshot()
    {
        lock (_gate)
        {
            int segmentCount = _logicalLines.Sum(line => line.Segments.Count);
            StoredLine[] lines = new StoredLine[segmentCount];
            int destinationIndex = 0;

            foreach (LogicalLine logicalLine in _logicalLines)
            {
                logicalLine.Segments.CopyTo(lines, destinationIndex);
                destinationIndex += logicalLine.Segments.Count;
            }

            ReadOnlyCollection<StoredLine> immutableLines = Array.AsReadOnly(lines);
            return new LineStoreSnapshot(
                _logicalLines.Count == 0 ? null : _logicalLines[0].LogicalId,
                _logicalLines.Count == 0 ? null : _logicalLines[^1].LogicalId,
                _evictedLineCount,
                immutableLines);
        }
    }

    public LineStoreSnapshot SnapshotAfter(LineCursor? cursor, int maximumSegments)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumSegments);
        lock (_gate)
        {
            List<StoredLine> lines = new(Math.Min(maximumSegments, 256));
            int logicalLineIndex = 0;
            if (cursor.HasValue)
            {
                long cursorLogicalId = cursor.Value.LogicalId;
                int low = 0;
                int high = _logicalLines.Count;
                while (low < high)
                {
                    int middle = low + (high - low) / 2;
                    if (_logicalLines[middle].LogicalId < cursorLogicalId)
                    {
                        low = middle + 1;
                    }
                    else
                    {
                        high = middle;
                    }
                }

                logicalLineIndex = low;
            }

            for (; logicalLineIndex < _logicalLines.Count; logicalLineIndex++)
            {
                LogicalLine logicalLine = _logicalLines[logicalLineIndex];
                foreach (StoredLine line in logicalLine.Segments)
                {
                    if (cursor.HasValue &&
                        (line.LogicalId < cursor.Value.LogicalId ||
                         line.LogicalId == cursor.Value.LogicalId && line.SegmentIndex <= cursor.Value.SegmentIndex))
                    {
                        continue;
                    }

                    lines.Add(line);
                    if (lines.Count == maximumSegments)
                    {
                        return CreateSnapshot(lines);
                    }
                }
            }

            return CreateSnapshot(lines);
        }
    }

    private LineStoreSnapshot CreateSnapshot(IReadOnlyList<StoredLine> lines) => new(
        _logicalLines.Count == 0 ? null : _logicalLines[0].LogicalId,
        _logicalLines.Count == 0 ? null : _logicalLines[^1].LogicalId,
        _evictedLineCount,
        Array.AsReadOnly(lines.ToArray()));

    private LogicalLine CreateLogicalLine(
        long logicalId,
        LineDirection direction,
        DateTimeOffset timestamp,
        string text,
        bool isTerminated)
    {
        int segmentCount = Math.Max(1, (text.Length + _maxSegmentCharacters - 1) / _maxSegmentCharacters);
        List<StoredLine> segments = new(segmentCount);
        int textBytes = 0;

        for (int segmentIndex = 0; segmentIndex < segmentCount; segmentIndex++)
        {
            int offset = segmentIndex * _maxSegmentCharacters;
            int length = Math.Min(_maxSegmentCharacters, text.Length - offset);
            string segmentText = text.Substring(offset, length);
            textBytes += Encoding.UTF8.GetByteCount(segmentText);
            segments.Add(new StoredLine(
                logicalId,
                segmentIndex,
                direction,
                timestamp,
                segmentText,
                isTerminated));
        }

        return new LogicalLine(logicalId, textBytes, segments);
    }

    private LogicalLine? FindLogicalLine(long logicalId)
    {
        for (int index = _logicalLines.Count - 1; index >= 0; index--)
        {
            LogicalLine line = _logicalLines[index];
            if (line.LogicalId == logicalId)
            {
                return line;
            }

            if (line.LogicalId < logicalId)
            {
                break;
            }
        }

        return null;
    }

    private sealed class LogicalLine(long logicalId, int textBytes, List<StoredLine> segments)
    {
        public long LogicalId { get; } = logicalId;

        public int TextBytes { get; set; } = textBytes;

        public List<StoredLine> Segments { get; } = segments;
    }
}
