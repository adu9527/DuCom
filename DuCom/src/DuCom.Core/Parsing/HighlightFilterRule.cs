namespace DuCom.Core.Parsing;

public readonly record struct HighlightFilterRule(
    Guid Id,
    string Name,
    HighlightFilterRuleKind Kind,
    RuleMatchMode Mode,
    string Pattern,
    bool IsCaseSensitive,
    bool IsEnabled,
    byte? ForegroundR,
    byte? ForegroundG,
    byte? ForegroundB,
    byte? BackgroundR,
    byte? BackgroundG,
    byte? BackgroundB,
    bool Bold = false,
    bool Italic = false)
{
    public bool HasForeground => ForegroundR.HasValue && ForegroundG.HasValue && ForegroundB.HasValue;

    public bool HasBackground => BackgroundR.HasValue && BackgroundG.HasValue && BackgroundB.HasValue;
}
