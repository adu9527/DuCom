namespace DuCom.Core.LogAnalysis;

public sealed record LogAnalyzerRule(
    Guid Id,
    string Name,
    string Comment,
    string Category,
    string Pattern,
    bool IsCaseSensitive,
    bool IsEnabled,
    byte? ForegroundR,
    byte? ForegroundG,
    byte? ForegroundB,
    byte? BackgroundR,
    byte? BackgroundG,
    byte? BackgroundB,
    bool IncludeInAnalysis = true);
