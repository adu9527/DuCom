namespace DuCom.Core.Diagnostics;

public enum WatchdogMatchMode
{
    Contains,
    Regex,
}

public static class WatchdogMatchModeHelper
{
    public static IReadOnlyList<WatchdogMatchMode> All { get; } =
        [WatchdogMatchMode.Contains, WatchdogMatchMode.Regex];
}

public enum WatchdogActionKind
{
    /// <summary>Shows a bilingual hint in the session warning surface.</summary>
    Hint,

    /// <summary>Writes an entry to the diagnostic log.</summary>
    DiagnosticLog,

    /// <summary>Sends an existing command string through the session.</summary>
    SendCommand,
}

public static class WatchdogActionKindHelper
{
    public static IReadOnlyList<WatchdogActionKind> All { get; } =
        [WatchdogActionKind.Hint, WatchdogActionKind.DiagnosticLog, WatchdogActionKind.SendCommand];
}

/// <summary>
/// One watchdog rule: the pattern is expected to appear at least once per
/// <see cref="ExpectWithinSeconds"/>. When it does not, the action fires, at most once per
/// <see cref="ThrottleSeconds"/>. Pure data.
/// </summary>
public sealed record WatchdogRule(
    Guid Id,
    string Name,
    string Pattern,
    WatchdogMatchMode Mode,
    bool IsCaseSensitive,
    bool IsEnabled,
    int ExpectWithinSeconds,
    int ThrottleSeconds,
    WatchdogActionKind ActionKind,
    string ActionCommand)
{
    public static WatchdogRule CreateDefault() => new(
        Guid.NewGuid(),
        string.Empty,
        string.Empty,
        WatchdogMatchMode.Contains,
        IsCaseSensitive: false,
        IsEnabled: true,
        ExpectWithinSeconds: 30,
        ThrottleSeconds: 60,
        WatchdogActionKind.Hint,
        ActionCommand: string.Empty);
}
