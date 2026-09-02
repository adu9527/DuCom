namespace DuCom.Core.Storage;

public sealed record LineStoreSnapshot(
    long? FirstLogicalId,
    long? LastLogicalId,
    long EvictedLineCount,
    IReadOnlyList<StoredLine> Lines);

public readonly record struct LineCursor(long LogicalId, int SegmentIndex);
