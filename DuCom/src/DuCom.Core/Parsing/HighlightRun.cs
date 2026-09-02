namespace DuCom.Core.Parsing;

public readonly record struct HighlightRun(
    string Text,
    byte? ForegroundR,
    byte? ForegroundG,
    byte? ForegroundB,
    byte? BackgroundR = null,
    byte? BackgroundG = null,
    byte? BackgroundB = null,
    bool Bold = false,
    bool Italic = false)
{
    public bool HasForeground => ForegroundR.HasValue && ForegroundG.HasValue && ForegroundB.HasValue;

    public bool HasBackground => BackgroundR.HasValue && BackgroundG.HasValue && BackgroundB.HasValue;

    /// <summary>A run segment carrying no rule coloring.</summary>
    public static HighlightRun Plain(string text) => new(text, null, null, null);
}
