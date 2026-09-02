namespace DuCom;

/// <summary>
/// Single source of truth for the tool-center page keys, their tab indices, and their
/// header resource keys. The tab indices must match the TabItem order in
/// ToolCenterWindow.xaml; the tools smoke test verifies every mapping against the actual
/// tab header, so a mismatch fails the gate instead of silently opening the wrong page.
/// Shortcut management is not a tool-center page anymore — it lives in the settings window.
/// </summary>
public static class ToolCenterPages
{
    public const string Plugins = "plugins";
    public const string Monitor = "monitor";
    public const string VirtualPort = "virtual-port";
    public const string Ascii = "ascii";
    public const string References = "references";
    public const string Telnet = "telnet";
    public const string Commands = "commands";
    public const string Watchdog = "watchdog";
    public const string SendHistory = "send-history";

    /// <summary>Every page key in tab order — used by the tools smoke test.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        Plugins, Monitor, VirtualPort, Ascii, References, Telnet, Commands, Watchdog, SendHistory,
    ];

    public static int IndexOf(string? page) => page switch
    {
        Plugins => 0,
        Monitor => 1,
        VirtualPort => 2,
        Ascii => 3,
        References => 4,
        Telnet => 5,
        Commands => 6,
        Watchdog => 7,
        SendHistory => 8,
        _ => 0,
    };

    public static string HeaderResourceKey(string? page) => page switch
    {
        Plugins => "Menu.Tools.Plugins",
        Monitor => "Menu.Tools.Monitor",
        VirtualPort => "Menu.Tools.VirtualPort",
        Ascii => "Menu.Tools.Ascii",
        References => "Menu.Tools.References",
        Telnet => "Menu.Tools.Telnet",
        Commands => "Menu.Tools.Commands",
        Watchdog => "Menu.Tools.Watchdog",
        SendHistory => "Menu.Tools.SendHistory",
        _ => "Menu.Tools.Plugins",
    };
}
