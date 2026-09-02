using DuCom.Core.Parsing;
using DuCom.Core.Storage;

namespace DuCom.ViewModels;

public sealed record LogLineViewModel(
    long LogicalId,
    int SegmentIndex,
    DateTimeOffset TimestampUtc,
    LineDirection Direction,
    string Text,
    IReadOnlyList<StyleRun> StyledRuns);
