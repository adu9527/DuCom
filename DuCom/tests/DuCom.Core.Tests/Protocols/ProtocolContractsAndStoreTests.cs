using System.Collections.ObjectModel;
using DuCom.Core.Protocols;

namespace DuCom.Core.Tests.Protocols;

public sealed class ProtocolContractsAndStoreTests
{
    [Fact]
    public void FrameCopiesBytesAndBuildsImmutableFieldsLazily()
    {
        byte[] source = [1, 2, 3];
        int builds = 0;
        ProtocolFrame frame = CreateFrame(1, source, () =>
        {
            builds++;
            return [new ProtocolField("Value", 2, 1, 1)];
        });

        source[0] = 99;
        Assert.Equal(0, builds);
        Assert.Equal([1, 2, 3], frame.GetRawBytes());
        Assert.Single(frame.Fields);
        Assert.Single(frame.Fields);
        Assert.Equal(1, builds);
        Assert.IsType<ReadOnlyCollection<ProtocolField>>(frame.Fields);
        Assert.Throws<NotSupportedException>(() => ((IList<ProtocolField>)frame.Fields).Add(new ProtocolField("x", 0, 0, 0)));
    }

    [Fact]
    public void DirectionAndSourceGenerationAreAdditiveAndDefaultToRx()
    {
        ProtocolDecoderContext compatibleContext = new(1, 2, 3, DateTimeOffset.UnixEpoch);
        (long generation, long sequence, long offset, DateTimeOffset timestamp) = compatibleContext;
        ProtocolFrame compatibleFrame = CreateFrame(1);
        Guid sourceGeneration = Guid.NewGuid();
        ProtocolDecoderContext txContext = new(4, 5, 6, DateTimeOffset.UnixEpoch, ProtocolTrafficDirection.Tx, sourceGeneration);
        ProtocolFrame txFrame = new(
            2, 4, 5, 5, 6, DateTimeOffset.UnixEpoch, [1],
            new ProtocolFrameSummary("Test", "Data", "test"),
            new IntegrityCheckResult(ProtocolIntegrityStatus.NotChecked, "None", null, null, -1, 0, 0, 0),
            direction: ProtocolTrafficDirection.Tx,
            sourceGeneration: sourceGeneration);

        Assert.Equal(ProtocolTrafficDirection.Rx, compatibleContext.Direction);
        Assert.Equal((1L, 2L, 3L, DateTimeOffset.UnixEpoch), (generation, sequence, offset, timestamp));
        Assert.Equal(Guid.Empty, compatibleContext.SourceGeneration);
        Assert.Equal(ProtocolTrafficDirection.Rx, compatibleFrame.Direction);
        Assert.Equal(Guid.Empty, compatibleFrame.SourceGeneration);
        Assert.Equal(ProtocolTrafficDirection.Tx, txContext.Direction);
        Assert.Equal(sourceGeneration, txFrame.SourceGeneration);
    }

    [Fact]
    public void StoreIsBoundedSupportsCursorAndResetsGenerationOnClear()
    {
        ProtocolFrameStore store = new(2);
        ProtocolFrameCursor first = store.Append(CreateFrame(1));
        store.Append(CreateFrame(2));
        store.Append(CreateFrame(3));

        ProtocolFrameSnapshot snapshot = store.SnapshotAfter(first, 10);
        Assert.Equal([2L, 3L], snapshot.Frames.Select(item => item.Frame.DecoderFrameId));
        Assert.Equal(1, snapshot.EvictedCount);
        Assert.False(snapshot.CursorReset);
        Assert.IsType<ReadOnlyCollection<StoredProtocolFrame>>(snapshot.Frames);

        store.Clear();
        store.Append(CreateFrame(4));
        ProtocolFrameSnapshot reset = store.SnapshotAfter(first, 10);
        Assert.True(reset.CursorReset);
        Assert.Single(reset.Frames);
        Assert.NotEqual(first.StoreGeneration, reset.StoreGeneration);
    }

    [Fact]
    public void EmptyAndClearedStoreHaveNoSequenceBounds()
    {
        ProtocolFrameStore store = new(1);
        ProtocolFrameSnapshot empty = store.Snapshot();
        Assert.Null(empty.FirstSequence);
        Assert.Null(empty.LastSequence);
        Assert.Empty(empty.Frames);
        Assert.Equal(0, empty.EvictedCount);
        Assert.False(empty.CursorReset);

        ProtocolFrameCursor cursor = store.Append(CreateFrame(1));
        store.Append(CreateFrame(2));
        store.Clear();
        ProtocolFrameSnapshot cleared = store.SnapshotAfter(cursor, 1);
        Assert.Null(cleared.FirstSequence);
        Assert.Null(cleared.LastSequence);
        Assert.Empty(cleared.Frames);
        Assert.Equal(0, cleared.EvictedCount);
        Assert.True(cleared.CursorReset);
        Assert.Equal(cursor.StoreGeneration + 1, cleared.StoreGeneration);

        store.Append(CreateFrame(3));
        ProtocolFrameSnapshot restarted = store.SnapshotAfter(cursor, 1);
        Assert.Equal(1L, restarted.FirstSequence);
        Assert.Equal(1L, restarted.LastSequence);
        Assert.Equal(1L, Assert.Single(restarted.Frames).StoreSequence);
        Assert.Equal(0, restarted.EvictedCount);
        Assert.True(restarted.CursorReset);
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(1, 2)]
    [InlineData(2, 3)]
    [InlineData(4, 0)]
    [InlineData(100, 0)]
    public void SnapshotBoundsDescribeWholeStoreRegardlessOfSelectedPage(long after, long expectedSequence)
    {
        ProtocolFrameStore store = new(3);
        ProtocolFrameCursor cursor = default;
        for (int index = 1; index <= 4; index++) cursor = store.Append(CreateFrame(index));

        ProtocolFrameSnapshot snapshot = store.SnapshotAfter(cursor with { StoreSequence = after }, 1);
        Assert.Equal(2L, snapshot.FirstSequence);
        Assert.Equal(4L, snapshot.LastSequence);
        Assert.Equal(1, snapshot.EvictedCount);
        Assert.Equal(cursor.StoreGeneration, snapshot.StoreGeneration);
        Assert.False(snapshot.CursorReset);
        if (expectedSequence == 0)
            Assert.Empty(snapshot.Frames);
        else
            Assert.Equal(expectedSequence, Assert.Single(snapshot.Frames).StoreSequence);
    }

    [Fact]
    public void SnapshotRemainsIndependentAfterEvictionAndClear()
    {
        ProtocolFrameStore store = new(1);
        ProtocolFrame frame = CreateFrame(1);
        ProtocolFrameCursor cursor = store.Append(frame);
        ProtocolFrameSnapshot snapshot = store.Snapshot();

        store.Append(CreateFrame(2));
        ProtocolFrameSnapshot evicted = store.Snapshot();
        Assert.Equal(2L, evicted.FirstSequence);
        Assert.Equal(2L, evicted.LastSequence);
        Assert.Equal(1, evicted.EvictedCount);
        store.Clear();
        store.Append(CreateFrame(3));

        Assert.Equal(cursor.StoreGeneration, snapshot.StoreGeneration);
        Assert.Equal(1L, snapshot.FirstSequence);
        Assert.Equal(1L, snapshot.LastSequence);
        Assert.Equal(0, snapshot.EvictedCount);
        Assert.Same(frame, Assert.Single(snapshot.Frames).Frame);
        Assert.Throws<NotSupportedException>(() => ((IList<StoredProtocolFrame>)snapshot.Frames).Clear());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SmallSnapshotAllocationDoesNotScaleWithRetainedFrameCount(bool atTail)
    {
        ProtocolFrameStore small = new(1);
        ProtocolFrameStore large = new(32_768);
        ProtocolFrame frame = CreateFrame(1);
        ProtocolFrameCursor smallTail = small.Append(frame);
        ProtocolFrameCursor largeTail = default;
        for (int index = 0; index < 32_768; index++) largeTail = large.Append(frame);
        ProtocolFrameCursor? smallCursor = atTail ? smallTail : null;
        ProtocolFrameCursor? largeCursor = atTail ? largeTail : null;

        for (int index = 0; index < 10; index++)
        {
            small.SnapshotAfter(smallCursor, 1);
            large.SnapshotAfter(largeCursor, 1);
        }

        // Minimum batch allocations exclude transient JIT/lazy-initialization noise.
        // A full-queue copy allocates on every call, so it cannot disappear between batches.
        long smallBytes = long.MaxValue;
        long largeBytes = long.MaxValue;
        for (int batch = 0; batch < 10; batch++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int index = 0; index < 10; index++) small.SnapshotAfter(smallCursor, 1);
            smallBytes = Math.Min(smallBytes, GC.GetAllocatedBytesForCurrentThread() - before);
            before = GC.GetAllocatedBytesForCurrentThread();
            for (int index = 0; index < 10; index++) large.SnapshotAfter(largeCursor, 1);
            largeBytes = Math.Min(largeBytes, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        Assert.True(largeBytes <= smallBytes + 4_096, $"Small store: {smallBytes} bytes; large store: {largeBytes} bytes.");
    }

    [Fact]
    public async Task StoreSupportsConcurrentAppendAndSnapshot()
    {
        ProtocolFrameStore store = new(500);
        using CancellationTokenSource done = new();
        Task reader = Task.Run(() =>
        {
            while (!done.IsCancellationRequested)
            {
                ProtocolFrameSnapshot snapshot = store.Snapshot();
                Assert.True(snapshot.Frames.Count <= 500);
                Assert.Equal(snapshot.Frames.Select(item => item.StoreSequence).Order(), snapshot.Frames.Select(item => item.StoreSequence));
                if (snapshot.Frames.Count == 0)
                {
                    Assert.Null(snapshot.FirstSequence);
                    Assert.Null(snapshot.LastSequence);
                }
                else
                {
                    Assert.Equal(snapshot.Frames[0].StoreSequence, snapshot.FirstSequence);
                    Assert.Equal(snapshot.Frames[^1].StoreSequence, snapshot.LastSequence);
                    Assert.Equal(snapshot.EvictedCount + 1, snapshot.FirstSequence);
                    Assert.Equal(snapshot.EvictedCount + snapshot.Frames.Count, snapshot.LastSequence);
                }
            }
        });
        Task[] writers = Enumerable.Range(0, 4).Select(worker => Task.Run(() =>
        {
            for (int index = 0; index < 500; index++) store.Append(CreateFrame(worker * 500 + index + 1));
        })).ToArray();

        await Task.WhenAll(writers);
        done.Cancel();
        await reader;
        Assert.Equal(500, store.Snapshot().Frames.Count);
    }

    private static ProtocolFrame CreateFrame(long id, byte[]? bytes = null, Func<IReadOnlyList<ProtocolField>>? fields = null) => new(
        id, 1, 1, 1, 0, DateTimeOffset.UnixEpoch, bytes ?? [1],
        new ProtocolFrameSummary("Test", "Data", "test"),
        new IntegrityCheckResult(ProtocolIntegrityStatus.NotChecked, "None", null, null, -1, 0, 0, 0),
        fieldsFactory: fields);
}
