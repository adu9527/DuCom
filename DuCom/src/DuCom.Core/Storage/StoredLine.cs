namespace DuCom.Core.Storage;

public readonly record struct StoredLine(
    long LogicalId,
    int SegmentIndex,
    LineDirection Direction,
    DateTimeOffset TimestampUtc,
    string Text,
    bool IsTerminated);
