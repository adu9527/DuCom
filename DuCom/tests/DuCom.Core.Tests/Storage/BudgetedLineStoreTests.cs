using System.Collections.ObjectModel;
using System.Text;
using DuCom.Core.Storage;

namespace DuCom.Core.Tests.Storage;

public sealed class BudgetedLineStoreTests
{
    [Fact]
    public void AppendSegmentsLongTextWithOneLogicalId()
    {
        BudgetedLineStore store = new(maxTextBytes: 100, maxSegmentCharacters: 3);
        DateTimeOffset timestamp = DateTimeOffset.UtcNow;

        store.Append(LineDirection.Rx, timestamp, "abcdefgh", isTerminated: true);

        LineStoreSnapshot snapshot = store.Snapshot();
        Assert.Equal(["abc", "def", "gh"], snapshot.Lines.Select(line => line.Text));
        Assert.Equal([0, 1, 2], snapshot.Lines.Select(line => line.SegmentIndex));
        Assert.Single(snapshot.Lines.Select(line => line.LogicalId).Distinct());
        Assert.All(snapshot.Lines, line =>
        {
            Assert.Equal(LineDirection.Rx, line.Direction);
            Assert.Equal(timestamp, line.TimestampUtc);
            Assert.True(line.IsTerminated);
        });
    }

    [Fact]
    public void AppendKeepsUtf8TextWithinBudget()
    {
        BudgetedLineStore store = new(maxTextBytes: 7, maxSegmentCharacters: 20);

        store.Append(LineDirection.Rx, DateTimeOffset.UtcNow, "1234", isTerminated: true);
        store.Append(LineDirection.Tx, DateTimeOffset.UtcNow, "éé", isTerminated: true);

        LineStoreSnapshot snapshot = store.Snapshot();
        int storedBytes = snapshot.Lines.Sum(line => Encoding.UTF8.GetByteCount(line.Text));
        Assert.True(storedBytes <= 7);
        Assert.Single(snapshot.Lines);
        Assert.Equal("éé", snapshot.Lines[0].Text);
        Assert.Equal(1, snapshot.EvictedLineCount);
    }

    [Fact]
    public void BudgetEvictsWholeOldestLogicalLine()
    {
        BudgetedLineStore store = new(maxTextBytes: 7, maxSegmentCharacters: 2);

        store.Append(LineDirection.Rx, DateTimeOffset.UtcNow, "abcd", isTerminated: true);
        store.Append(LineDirection.Tx, DateTimeOffset.UtcNow, "wxyz", isTerminated: false);

        LineStoreSnapshot snapshot = store.Snapshot();
        Assert.Equal(["wx", "yz"], snapshot.Lines.Select(line => line.Text));
        Assert.All(snapshot.Lines, line => Assert.Equal(2, line.LogicalId));
        Assert.Equal(1, snapshot.EvictedLineCount);
        Assert.Equal(2, snapshot.FirstLogicalId);
        Assert.Equal(2, snapshot.LastLogicalId);
    }

    [Fact]
    public void LogicalIdsRemainStableAndIncreaseAcrossEviction()
    {
        BudgetedLineStore store = new(maxTextBytes: 2, maxSegmentCharacters: 10);

        store.Append(LineDirection.Rx, DateTimeOffset.UtcNow, "aa", isTerminated: true);
        store.Append(LineDirection.Rx, DateTimeOffset.UtcNow, "bb", isTerminated: true);
        store.Append(LineDirection.Rx, DateTimeOffset.UtcNow, "cc", isTerminated: true);

        LineStoreSnapshot snapshot = store.Snapshot();
        Assert.Single(snapshot.Lines);
        Assert.Equal(3, snapshot.Lines[0].LogicalId);
        Assert.Equal(2, snapshot.EvictedLineCount);
    }

    [Fact]
    public void OversizedLogicalLineIsFullyEvicted()
    {
        BudgetedLineStore store = new(maxTextBytes: 3, maxSegmentCharacters: 2);

        store.Append(LineDirection.Rx, DateTimeOffset.UtcNow, "abcd", isTerminated: false);

        LineStoreSnapshot snapshot = store.Snapshot();
        Assert.Empty(snapshot.Lines);
        Assert.Null(snapshot.FirstLogicalId);
        Assert.Null(snapshot.LastLogicalId);
        Assert.Equal(1, snapshot.EvictedLineCount);
    }

    [Fact]
    public void ClearRemovesDisplayLinesAndAccumulatesEvictionsWithoutReusingIds()
    {
        BudgetedLineStore store = new(maxTextBytes: 100, maxSegmentCharacters: 10);
        store.Append(LineDirection.Rx, DateTimeOffset.UtcNow, "first", isTerminated: true);
        store.Append(LineDirection.System, DateTimeOffset.UtcNow, "second", isTerminated: true);

        store.Clear();

        LineStoreSnapshot cleared = store.Snapshot();
        Assert.Empty(cleared.Lines);
        Assert.Null(cleared.FirstLogicalId);
        Assert.Null(cleared.LastLogicalId);
        Assert.Equal(2, cleared.EvictedLineCount);

        store.Append(LineDirection.Tx, DateTimeOffset.UtcNow, "third", isTerminated: false);

        LineStoreSnapshot appended = store.Snapshot();
        Assert.Equal(3, appended.Lines[0].LogicalId);
        Assert.Equal(2, appended.EvictedLineCount);
    }

    [Fact]
    public void SnapshotReturnsAnImmutableIndependentCopy()
    {
        BudgetedLineStore store = new(maxTextBytes: 100, maxSegmentCharacters: 10);
        store.Append(LineDirection.Rx, DateTimeOffset.UtcNow, "first", isTerminated: true);

        LineStoreSnapshot snapshot = store.Snapshot();
        store.Clear();

        Assert.Single(snapshot.Lines);
        Assert.IsType<ReadOnlyCollection<StoredLine>>(snapshot.Lines);
        Assert.Throws<NotSupportedException>(() => ((IList<StoredLine>)snapshot.Lines).Add(default));
    }

    [Fact]
    public async Task AppendAndSnapshotAreThreadSafe()
    {
        const int writerCount = 4;
        const int linesPerWriter = 500;
        BudgetedLineStore store = new(maxTextBytes: 1_000_000, maxSegmentCharacters: 8);
        using CancellationTokenSource snapshotsComplete = new();

        Task reader = Task.Run(() =>
        {
            while (!snapshotsComplete.IsCancellationRequested)
            {
                AssertSnapshotIsConsistent(store.Snapshot());
            }
        });

        Task[] writers = Enumerable.Range(0, writerCount)
            .Select(writer => Task.Run(() =>
            {
                for (int index = 0; index < linesPerWriter; index++)
                {
                    store.Append(LineDirection.Rx, DateTimeOffset.UtcNow, $"{writer}:{index:D4}:payload", true);
                }
            }))
            .ToArray();

        await Task.WhenAll(writers);
        snapshotsComplete.Cancel();
        await reader;

        LineStoreSnapshot snapshot = store.Snapshot();
        AssertSnapshotIsConsistent(snapshot);
        Assert.Equal(writerCount * linesPerWriter, snapshot.Lines.Select(line => line.LogicalId).Distinct().Count());
        Assert.Equal(writerCount * linesPerWriter, snapshot.LastLogicalId);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    public void ConstructorRejectsNonPositiveLimits(int maxTextBytes, int maxSegmentCharacters)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BudgetedLineStore(maxTextBytes, maxSegmentCharacters));
    }

    [Fact]
    public void ManyContinuationsPreserveSegmentOrderAndEvictTheLogicalLineOnce()
    {
        BudgetedLineStore store = new(maxTextBytes: 1_000, maxSegmentCharacters: 1);
        long logicalId = store.Append(LineDirection.Rx, DateTimeOffset.UtcNow, string.Empty, isTerminated: false);

        for (int index = 0; index < 500; index++)
        {
            store.AppendContinuation(logicalId, "x", isTerminated: false);
        }

        LineStoreSnapshot beforeEviction = store.Snapshot();
        Assert.Equal(501, beforeEviction.Lines.Count);
        Assert.Equal(Enumerable.Range(0, 501), beforeEviction.Lines.Select(line => line.SegmentIndex));

        store.AppendContinuation(logicalId, new string('y', 1_001), isTerminated: true);
        LineStoreSnapshot afterEviction = store.Snapshot();
        Assert.Empty(afterEviction.Lines);
        Assert.Equal(1, afterEviction.EvictedLineCount);
    }

    [Fact]
    public void SnapshotAfter_StartsAtCursorLogicalLineAndReturnsOnlyLaterSegments()
    {
        BudgetedLineStore store = new(maxTextBytes: 10_000, maxSegmentCharacters: 2);
        for (int index = 0; index < 100; index++)
        {
            store.Append(LineDirection.Rx, DateTimeOffset.UtcNow, $"{index:D4}", isTerminated: true);
        }

        LineStoreSnapshot snapshot = store.SnapshotAfter(new LineCursor(75, 0), maximumSegments: 8);

        Assert.Equal([75L, 76L, 76L, 77L, 77L, 78L, 78L, 79L], snapshot.Lines.Select(line => line.LogicalId));
        Assert.Equal([1, 0, 1, 0, 1, 0, 1, 0], snapshot.Lines.Select(line => line.SegmentIndex));
    }

    private static void AssertSnapshotIsConsistent(LineStoreSnapshot snapshot)
    {
        StoredLine[] lines = snapshot.Lines.ToArray();
        if (lines.Length == 0)
        {
            Assert.Null(snapshot.FirstLogicalId);
            Assert.Null(snapshot.LastLogicalId);
            return;
        }

        Assert.Equal(lines[0].LogicalId, snapshot.FirstLogicalId);
        Assert.Equal(lines[^1].LogicalId, snapshot.LastLogicalId);

        foreach (IGrouping<long, StoredLine> logicalLine in lines.GroupBy(line => line.LogicalId))
        {
            Assert.Equal(Enumerable.Range(0, logicalLine.Count()), logicalLine.Select(line => line.SegmentIndex));
        }
    }
}
