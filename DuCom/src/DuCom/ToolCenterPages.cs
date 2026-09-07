namespace DuCom;

/// <summary>
/// Single source of truth for the tool-center page keys, their tab indices, and their
/// header resource keys. The tab indices must match the TabItem order in
/// ToolCenterWindow.xaml; the tools smoke test verifies every mapping against the actual
/// tab header, so a mismatch fails the gate instead of silently opening the wrong page.
/// Shortcut management is not a tool-center page anymore — it lives in the settings window.
/// Command groups have their own dedicated editor window (CommandGroupsWindow), and
/// plugin management lives in Settings rather than duplicating that page here.
/// </summary>
public static class ToolCenterPages
{
    public const string Monitor = "monitor";
    public const string VirtualPort = "virtual-port";
    public const string Ascii = "ascii";
    public const string References = "references";
    public const string Telnet = "telnet";
    public const string Watchdog = "watchdog";
    public const string SendHistory = "send-history";

    /// <summary>Every page key in tab order — used by the tools smoke test.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        Monitor, VirtualPort, Ascii, References, Telnet, Watchdog, SendHistory,
    ];

    public static int IndexOf(string? page) => page switch
    {
        Monitor => 0,
        VirtualPort => 1,
        Ascii => 2,
        References => 3,
        Telnet => 4,
        Watchdog => 5,
        SendHistory => 6,
        _ => 0,
    };

    public static string HeaderResourceKey(string? page) => page switch
    {
        Monitor => "Menu.Tools.Monitor",
        VirtualPort => "Menu.Tools.VirtualPort",
        Ascii => "Menu.Tools.Ascii",
        References => "Menu.Tools.References",
        Telnet => "Menu.Tools.Telnet",
        Watchdog => "Menu.Tools.Watchdog",
        SendHistory => "Menu.Tools.SendHistory",
        _ => "Menu.Tools.Monitor",
    };
}
