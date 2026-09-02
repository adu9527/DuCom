namespace DuCom.Core.Parsing;

public readonly record struct AnsiStyle(
    byte? ForegroundR,
    byte? ForegroundG,
    byte? ForegroundB,
    byte? BackgroundR,
    byte? BackgroundG,
    byte? BackgroundB,
    bool Bold,
    bool Underline,
    bool Reverse)
{
    public static AnsiStyle Default { get; } = new(
        null, null, null,
        null, null, null,
        false, false, false);

    public bool HasForeground => ForegroundR.HasValue && ForegroundG.HasValue && ForegroundB.HasValue;

    public bool HasBackground => BackgroundR.HasValue && BackgroundG.HasValue && BackgroundB.HasValue;
}
