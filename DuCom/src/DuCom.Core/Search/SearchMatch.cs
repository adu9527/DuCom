namespace DuCom.Core.Search;

public readonly record struct SearchMatch(long LogicalId, int SegmentIndex, int StartIndex, int Length);
