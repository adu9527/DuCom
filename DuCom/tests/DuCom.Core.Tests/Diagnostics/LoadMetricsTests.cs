using DuCom.Core.Diagnostics;

namespace DuCom.Core.Tests.Diagnostics;

public sealed class LoadMetricsTests
{
    [Fact]
    public void SnapshotCapturesAllCountersAndQueuePeaks()
    {
        LoadMetrics metrics = new();

        metrics.AddProducedBlock(10);
        metrics.AddAcceptedBlock(10);
        metrics.AddFormattedLogBlock();
        metrics.AddWrittenLogRecord(14);
        metrics.AddLineRecords(2);
        metrics.AddEvictions(3);
        metrics.ObserveReceiveQueueDepth(4);
        metrics.ObserveReceiveQueueDepth(2);
        metrics.ObserveLogQueueDepth(7);
        metrics.AddFault();
        metrics.SetShutdownDrain(ShutdownDrainState.Completed, TimeSpan.FromMilliseconds(12));

        PipelineMetricsSnapshot snapshot = metrics.Snapshot();

        Assert.Equal(1, snapshot.ProducedBlocks);
        Assert.Equal(10, snapshot.ProducedBytes);
        Assert.Equal(1, snapshot.AcceptedBlocks);
        Assert.Equal(10, snapshot.AcceptedBytes);
        Assert.Equal(1, snapshot.FormattedLogBlocks);
        Assert.Equal(1, snapshot.WrittenLogRecords);
        Assert.Equal(14, snapshot.WrittenLogBytes);
        Assert.Equal(2, snapshot.LineRecords);
        Assert.Equal(3, snapshot.Evictions);
        Assert.Equal(4, snapshot.ReceiveQueuePeak);
        Assert.Equal(7, snapshot.LogQueuePeak);
        Assert.Equal(1, snapshot.Faults);
        Assert.Equal(ShutdownDrainState.Completed, snapshot.ShutdownDrainState);
        Assert.Equal(TimeSpan.FromMilliseconds(12), snapshot.ShutdownDrainDuration);
        Assert.True(snapshot.IsInputAcceptanceComplete);
        Assert.True(snapshot.IsLogFormattingCoverageComplete);
    }

    [Fact]
    public async Task ConcurrentUpdatesAreNotLost()
    {
        LoadMetrics metrics = new();

        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            for (int index = 0; index < 10_000; index++)
            {
                metrics.AddProducedBlock(32);
                metrics.AddAcceptedBlock(32);
                metrics.AddFormattedLogBlock();
            }
        })));

        PipelineMetricsSnapshot snapshot = metrics.Snapshot();

        Assert.Equal(40_000, snapshot.ProducedBlocks);
        Assert.Equal(1_280_000, snapshot.ProducedBytes);
        Assert.Equal(snapshot.ProducedBlocks, snapshot.AcceptedBlocks);
        Assert.Equal(snapshot.AcceptedBlocks, snapshot.FormattedLogBlocks);
    }
}
