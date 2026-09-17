namespace DuCom.Core.Diagnostics;

public sealed record ReceiveDiagnosticSnapshot(
    string PortName,
    string Trigger,
    double CallbackGapMilliseconds,
    int InitialBytesAvailable,
    int ReadBlocks,
    int ReadBytes,
    int MaximumReadBytes,
    int QueueDepthPeak,
    int RemainingBytesAvailable,
    bool CapacityLimited,
    int FormattedLines,
    double ProcessingMilliseconds);
