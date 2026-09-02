namespace DuCom.Core.Diagnostics;

public enum ShutdownDrainState
{
    NotStarted,
    InProgress,
    Completed,
    Faulted,
    Cancelled,
}

public sealed record PipelineMetricsSnapshot(
    long ProducedBlocks,
    long ProducedBytes,
    long AcceptedBlocks,
    long AcceptedBytes,
    long FormattedLogBlocks,
    long WrittenLogRecords,
    long WrittenLogBytes,
    long LineRecords,
    long Evictions,
    long ReceiveQueuePeak,
    long LogQueuePeak,
    long Faults,
    ShutdownDrainState ShutdownDrainState,
    TimeSpan ShutdownDrainDuration)
{
    public bool IsInputAcceptanceComplete => ProducedBlocks == AcceptedBlocks && ProducedBytes == AcceptedBytes;

    public bool IsLogFormattingCoverageComplete => AcceptedBlocks == FormattedLogBlocks;
}
