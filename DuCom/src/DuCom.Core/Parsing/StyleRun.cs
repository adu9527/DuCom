namespace DuCom.Core.Parsing;

/// <summary>
/// A display-ready text run carrying resolved visual styling. Pure data; no UI dependency.
/// </summary>
public readonly record struct StyleRun(
    string Text,
    byte? ForegroundR,
    byte? ForegroundG,
    byte? ForegroundB,
    byte? BackgroundR,
    byte? BackgroundG,
    byte? BackgroundB,
    bool Bold,
    bool Underline,
    bool Inverse,
    bool Italic = false)
{
    public bool HasForeground => ForegroundR.HasValue && ForegroundG.HasValue && ForegroundB.HasValue;

    public bool HasBackground => BackgroundR.HasValue && BackgroundG.HasValue && BackgroundB.HasValue;
}
