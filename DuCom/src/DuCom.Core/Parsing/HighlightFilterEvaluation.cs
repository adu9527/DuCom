namespace DuCom.Core.Parsing;

public readonly record struct HighlightFilterEvaluation(
    bool IsVisible,
    IReadOnlyList<HighlightRun> HighlightRuns,
    bool HasRegexTimeout = false);
