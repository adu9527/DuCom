using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DuCom.Core.Ports;
using DuCom.Core.Parsing;
using DuCom.Core.Persistence;
using DuCom.Core.Sending;
using DuCom.Services;
using Microsoft.Win32;

namespace DuCom.ViewModels;

public partial class MainViewModel
{
    public IReadOnlyList<int> DataBitsOptions { get; } = [5, 6, 7, 8];

    public IReadOnlyList<StopBits> StopBitsOptions { get; } = [StopBits.One, StopBits.Two, StopBits.OnePointFive];

    public IReadOnlyList<Parity> ParityOptions { get; } = Enum.GetValues<Parity>();

    public IReadOnlyList<Handshake> HandshakeOptions { get; } = Enum.GetValues<Handshake>();

    public IReadOnlyList<string> EncodingOptions { get; } = [Encoding.UTF8.WebName, Encoding.ASCII.WebName, "gb2312", "gbk"];

    public IReadOnlyList<ReceiveDisplayMode> ReceiveModeOptions { get; } = Enum.GetValues<ReceiveDisplayMode>();

    [ObservableProperty]
    public partial int BaudRate { get; set; } = 1_152_000;

    [ObservableProperty]
    public partial int DataBits { get; set; } = 8;

    [ObservableProperty]
    public partial StopBits StopBits { get; set; } = StopBits.One;

    [ObservableProperty]
    public partial Parity Parity { get; set; } = Parity.None;

    [ObservableProperty]
    public partial Handshake Handshake { get; set; } = Handshake.None;

    [ObservableProperty]
    public partial string EncodingName { get; set; } = Encoding.UTF8.WebName;

    [ObservableProperty]
    public partial ReceiveDisplayMode ReceiveMode { get; set; } = ReceiveDisplayMode.Str;

    [ObservableProperty]
    public partial bool TimestampEnabled { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimestampPreview))]
    public partial string TimestampFormat { get; set; } = "HH:mm:ss.fff";

    public IReadOnlyList<string> TimestampFormatOptions { get; } =
        ["HH:mm:ss", "HH:mm:ss.fff", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss.fff"];

    public string TimestampPreview => $"[{DateTimeOffset.Now.ToString(TimestampFormat, CultureInfo.InvariantCulture)}] Device boot complete";

    [ObservableProperty]
    public partial bool LoggingEnabled { get; set; } = true;

    private SettingsWindow? _settingsWindow;
    private SettingsWindow? _serialParametersWindow;
    private SessionViewModel? _portSettingsTargetSession;

    [ObservableProperty]
    public partial bool IsSidebarVisible { get; set; } = true;

    [ObservableProperty]
    public partial bool IsBottomSendVisible { get; set; } = true;

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool ShowHiddenPorts { get; set; }

    [ObservableProperty]
    public partial bool ShowSerialPorts { get; set; } = true;

    [ObservableProperty]
    public partial bool ShowVirtualPorts { get; set; } = true;

    [ObservableProperty]
    public partial bool ShowCoverPage { get; set; } = true;

    [ObservableProperty]
    public partial bool CoverPageAnimationEnabled { get; set; } = true;

    [ObservableProperty]
    public partial PortSortMode PortSortMode { get; set; } = PortSortMode.NameAscending;

    [ObservableProperty]
    public partial bool WordWrap { get; set; }

    [ObservableProperty]
    public partial bool ShowLineNumbers { get; set; } = true;

    [ObservableProperty]
    public partial bool HighlightCurrentLine { get; set; } = true;

    [ObservableProperty]
    public partial bool ShowControlCharacters { get; set; }

    [ObservableProperty]
    public partial bool ShowSpaces { get; set; }

    [ObservableProperty]
    public partial bool ShowTabs { get; set; }

    [ObservableProperty]
    public partial double LogFontSize { get; set; } = 14;

    [ObservableProperty]
    public partial string LogFontFamily { get; set; } = "Cascadia Mono";

    public IReadOnlyList<string> LogFontFamilies { get; } = [.. Fonts.SystemFontFamilies
        .Select(font => font.Source)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Order(StringComparer.OrdinalIgnoreCase)];

    [ObservableProperty]
    public partial SplitLayoutOrientation SplitOrientation { get; set; } = SplitLayoutOrientation.Vertical;

    [ObservableProperty]
    public partial double SplitterRatio { get; set; } = 0.5d;

    [ObservableProperty]
    public partial SendMode DefaultSendMode { get; set; } = SendMode.Str;

    [ObservableProperty]
    public partial NewlinePolicy DefaultNewline { get; set; } = NewlinePolicy.None;

    [ObservableProperty]
    public partial bool FreezeAfterSend { get; set; }

    [ObservableProperty]
    public partial bool SendPrefixEnabled { get; set; } = true;

    [ObservableProperty]
    public partial string SendPrefix { get; set; } = "TX > ";

    [ObservableProperty]
    public partial bool PauseFollowOnMouseWheel { get; set; } = true;

    [ObservableProperty]
    public partial bool PauseFollowOnFocus { get; set; }

    [ObservableProperty]
    public partial bool ShowPauseHint { get; set; } = true;

    [ObservableProperty]
    public partial bool AutoBackupEnabled { get; set; } = true;

    [ObservableProperty]
    public partial int AutoBackupPeriodDays { get; set; } = 7;

    [ObservableProperty]
    public partial bool PreventSleep { get; set; }

    [ObservableProperty]
    public partial bool CloseToTaskbar { get; set; }

    [ObservableProperty]
    public partial bool AutoCheckUpdates { get; set; } = true;

    public string? SkippedUpdateVersion { get; set; }

    [ObservableProperty]
    public partial bool ShowPortType { get; set; }

    [ObservableProperty]
    public partial double SearchOpacity { get; set; } = 1d;

    [ObservableProperty]
    public partial int TelnetPort { get; set; } = 23;

    [ObservableProperty]
    public partial bool TelnetAllowRemote { get; set; }

    [ObservableProperty]
    public partial bool TelnetAuthenticationEnabled { get; set; }

    [ObservableProperty]
    public partial string TelnetUsername { get; set; } = string.Empty;
}
