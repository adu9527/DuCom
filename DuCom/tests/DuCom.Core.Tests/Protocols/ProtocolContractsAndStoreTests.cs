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
