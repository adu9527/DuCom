using System.Globalization;
using System.IO.Ports;
using System.Text;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using DuCom.Core.Parsing;
using DuCom.Core.Sending;
using DuCom.Services;

namespace DuCom.ViewModels;

[Flags]
public enum ApplicationSettingChangeCategory
{
    Persist = 1,
    Transport = 2,
    PortVisibility = 4,
    PreventSleep = 8,
    LogFileNamePreview = 16,
    PrivateMemoryMonitor = 32,
}

public sealed class ApplicationSettingChangedEventArgs(
    string propertyName,
    ApplicationSettingChangeCategory category,
    object? value) : EventArgs
{
    public string PropertyName { get; } = propertyName;
    public ApplicationSettingChangeCategory Category { get; } = category;
    public object? Value { get; } = value;
}

public partial class ApplicationSettingsViewModel : ObservableObject
{
    public ApplicationSettingsViewModel(IEnumerable<string>? logFontFamilies = null)
    {
        LogFontFamilies = [.. (logFontFamilies ?? Fonts.SystemFontFamilies.Select(font => font.Source))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)];
    }

    public event EventHandler<ApplicationSettingChangedEventArgs>? SettingChanged;

    public IReadOnlyList<int> DataBitsOptions => SettingsCatalog.DataBitsOptions;
    public IReadOnlyList<StopBits> StopBitsOptions => SettingsCatalog.StopBitsOptions;
    public IReadOnlyList<Parity> ParityOptions => SettingsCatalog.ParityOptions;
    public IReadOnlyList<Handshake> HandshakeOptions => SettingsCatalog.HandshakeOptions;
    public IReadOnlyList<string> EncodingOptions => SettingsCatalog.EncodingOptions;
    public IReadOnlyList<ReceiveDisplayMode> ReceiveModeOptions => SettingsCatalog.ReceiveModeOptions;
    public IReadOnlyList<string> TimestampFormatOptions => SettingsCatalog.TimestampFormatOptions;
    public IReadOnlyList<string> LogFontFamilies { get; }

    [ObservableProperty] public partial int BaudRate { get; set; } = SettingsCatalog.DefaultBaudRate;
    [ObservableProperty] public partial int DataBits { get; set; } = SettingsCatalog.DefaultDataBits;
    [ObservableProperty] public partial StopBits StopBits { get; set; } = StopBits.One;
    [ObservableProperty] public partial Parity Parity { get; set; } = Parity.None;
    [ObservableProperty] public partial Handshake Handshake { get; set; } = Handshake.None;
    [ObservableProperty] public partial string EncodingName { get; set; } = Encoding.UTF8.WebName;
    [ObservableProperty] public partial ReceiveDisplayMode ReceiveMode { get; set; } = ReceiveDisplayMode.Str;
    [ObservableProperty] public partial bool TimestampEnabled { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimestampPreview))]
    public partial string TimestampFormat { get; set; } = SettingsCatalog.DefaultTimestampFormat;

    public string TimestampPreview => $"[{DateTimeOffset.Now.ToString(TimestampFormat, CultureInfo.InvariantCulture)}] Device boot complete";

    [ObservableProperty] public partial bool LoggingEnabled { get; set; } = true;
    [ObservableProperty] public partial string LogDirectory { get; set; } = SettingsCatalog.DefaultLogDirectory;
    [ObservableProperty] public partial int LogRotationMegabytes { get; set; } = SettingsCatalog.DefaultLogRotationMegabytes;
    [ObservableProperty] public partial bool LogRotationEnabled { get; set; } = true;
    [ObservableProperty] public partial int DisplayBudgetMegabytes { get; set; } = SettingsCatalog.DefaultDisplayBudgetMegabytes;
    [ObservableProperty] public partial string LogFileNameFormat { get; set; } = SettingsCatalog.DefaultLogFileNameFormat;
    [ObservableProperty] public partial bool IsSidebarVisible { get; set; } = true;
    [ObservableProperty] public partial bool IsBottomSendVisible { get; set; } = true;
    [ObservableProperty] public partial bool ShowHiddenPorts { get; set; }
    [ObservableProperty] public partial bool ShowSerialPorts { get; set; } = true;
    [ObservableProperty] public partial bool ShowVirtualPorts { get; set; } = true;
    [ObservableProperty] public partial bool ShowCoverPage { get; set; } = true;
    [ObservableProperty] public partial bool CoverPageAnimationEnabled { get; set; } = true;
    [ObservableProperty] public partial PortSortMode PortSortMode { get; set; } = PortSortMode.NameAscending;
    [ObservableProperty] public partial bool WordWrap { get; set; }
    [ObservableProperty] public partial bool ShowLineNumbers { get; set; } = true;
    [ObservableProperty] public partial bool HighlightCurrentLine { get; set; } = true;
    [ObservableProperty] public partial bool ShowControlCharacters { get; set; }
    [ObservableProperty] public partial bool ShowSpaces { get; set; }
    [ObservableProperty] public partial bool ShowTabs { get; set; }
    [ObservableProperty] public partial double LogFontSize { get; set; } = SettingsCatalog.DefaultLogFontSize;
    [ObservableProperty] public partial string LogFontFamily { get; set; } = SettingsCatalog.DefaultLogFontFamily;
    [ObservableProperty] public partial SplitLayoutOrientation SplitOrientation { get; set; } = SplitLayoutOrientation.Vertical;
    [ObservableProperty] public partial double SplitterRatio { get; set; } = 0.5d;
    [ObservableProperty] public partial SendMode DefaultSendMode { get; set; } = SendMode.Str;
    [ObservableProperty] public partial NewlinePolicy DefaultNewline { get; set; } = NewlinePolicy.None;
    [ObservableProperty] public partial bool FreezeAfterSend { get; set; }
    [ObservableProperty] public partial bool SendPrefixEnabled { get; set; } = true;
    [ObservableProperty] public partial string SendPrefix { get; set; } = "TX > ";
    [ObservableProperty] public partial bool PauseFollowOnMouseWheel { get; set; } = true;
    [ObservableProperty] public partial bool PauseFollowOnFocus { get; set; }
    [ObservableProperty] public partial bool ShowPauseHint { get; set; } = true;
    [ObservableProperty] public partial bool AutoBackupEnabled { get; set; } = true;
    [ObservableProperty] public partial int AutoBackupPeriodDays { get; set; } = SettingsCatalog.DefaultAutoBackupPeriodDays;
    [ObservableProperty] public partial bool PreventSleep { get; set; }
    [ObservableProperty] public partial bool CloseToTaskbar { get; set; }
    [ObservableProperty] public partial bool AutoCheckUpdates { get; set; } = true;
    public string? SkippedUpdateVersion { get; set; }
    [ObservableProperty] public partial bool ShowPortType { get; set; }
    [ObservableProperty] public partial double SearchOpacity { get; set; } = 1d;
    [ObservableProperty] public partial int TelnetPort { get; set; } = SettingsCatalog.DefaultTelnetPort;
    [ObservableProperty] public partial bool TelnetAllowRemote { get; set; }
    [ObservableProperty] public partial bool TelnetAuthenticationEnabled { get; set; }
    [ObservableProperty] public partial string TelnetUsername { get; set; } = string.Empty;
    [ObservableProperty] public partial bool PrivateMemoryMonitorEnabled { get; set; }
    [ObservableProperty] public partial int PrivateMemoryThresholdMiB { get; set; } = SettingsCatalog.DefaultPrivateMemoryThresholdMiB;
    [ObservableProperty] public partial bool ShowMemoryMonitor { get; set; } = true;
    [ObservableProperty] public partial int MemoryRefreshInterval { get; set; } = SettingsCatalog.DefaultMemoryRefreshInterval;
    [ObservableProperty] public partial int SystemMemoryRefreshInterval { get; set; } = SettingsCatalog.DefaultSystemMemoryRefreshInterval;

    protected void RestoreApplicationSettingDefaults()
    {
        BaudRate = SettingsCatalog.DefaultBaudRate;
        DataBits = SettingsCatalog.DefaultDataBits;
        StopBits = StopBits.One;
        Parity = Parity.None;
        Handshake = Handshake.None;
        EncodingName = Encoding.UTF8.WebName;
        ReceiveMode = ReceiveDisplayMode.Str;
        TimestampEnabled = true;
        TimestampFormat = SettingsCatalog.DefaultTimestampFormat;
        LoggingEnabled = true;
        LogDirectory = SettingsCatalog.DefaultLogDirectory;
        DefaultSendMode = SendMode.Str;
        LogRotationMegabytes = SettingsCatalog.DefaultLogRotationMegabytes;
        LogRotationEnabled = true;
        DisplayBudgetMegabytes = SettingsCatalog.DefaultDisplayBudgetMegabytes;
        PrivateMemoryMonitorEnabled = false;
        PrivateMemoryThresholdMiB = SettingsCatalog.DefaultPrivateMemoryThresholdMiB;
        ShowMemoryMonitor = true;
        MemoryRefreshInterval = SettingsCatalog.DefaultMemoryRefreshInterval;
        SystemMemoryRefreshInterval = SettingsCatalog.DefaultSystemMemoryRefreshInterval;
        LogFileNameFormat = SettingsCatalog.DefaultLogFileNameFormat;
        FreezeAfterSend = false;
        SendPrefixEnabled = true;
        SendPrefix = "TX > ";
        ShowPortType = false;
        PauseFollowOnMouseWheel = true;
        PauseFollowOnFocus = false;
        ShowPauseHint = true;
        AutoBackupEnabled = true;
        AutoBackupPeriodDays = SettingsCatalog.DefaultAutoBackupPeriodDays;
        PreventSleep = false;
        CloseToTaskbar = false;
        AutoCheckUpdates = true;
        SkippedUpdateVersion = null;
        SearchOpacity = 1d;
        DefaultNewline = NewlinePolicy.None;
        IsSidebarVisible = true;
        IsBottomSendVisible = true;
        ShowHiddenPorts = false;
        ShowSerialPorts = true;
        ShowVirtualPorts = true;
        ShowCoverPage = true;
        CoverPageAnimationEnabled = true;
        PortSortMode = PortSortMode.NameAscending;
        WordWrap = false;
        ShowLineNumbers = true;
        HighlightCurrentLine = true;
        ShowControlCharacters = false;
        ShowSpaces = false;
        ShowTabs = false;
        LogFontSize = SettingsCatalog.DefaultLogFontSize;
        LogFontFamily = SettingsCatalog.DefaultLogFontFamily;
        TelnetPort = SettingsCatalog.DefaultTelnetPort;
        TelnetAllowRemote = false;
    }

    private void RaiseSettingChanged(string propertyName, object? value, ApplicationSettingChangeCategory category = ApplicationSettingChangeCategory.Persist) =>
        SettingChanged?.Invoke(this, new ApplicationSettingChangedEventArgs(propertyName, category, value));

    partial void OnBaudRateChanged(int value) => RaiseSettingChanged(nameof(BaudRate), value, ApplicationSettingChangeCategory.Persist | ApplicationSettingChangeCategory.Transport);
    partial void OnDataBitsChanged(int value) => RaiseSettingChanged(nameof(DataBits), value, ApplicationSettingChangeCategory.Persist | ApplicationSettingChangeCategory.Transport);
    partial void OnStopBitsChanged(StopBits value) => RaiseSettingChanged(nameof(StopBits), value, ApplicationSettingChangeCategory.Persist | ApplicationSettingChangeCategory.Transport);
    partial void OnParityChanged(Parity value) => RaiseSettingChanged(nameof(Parity), value, ApplicationSettingChangeCategory.Persist | ApplicationSettingChangeCategory.Transport);
    partial void OnHandshakeChanged(Handshake value) => RaiseSettingChanged(nameof(Handshake), value, ApplicationSettingChangeCategory.Persist | ApplicationSettingChangeCategory.Transport);
    partial void OnEncodingNameChanged(string value) => RaiseSettingChanged(nameof(EncodingName), value, ApplicationSettingChangeCategory.Persist | ApplicationSettingChangeCategory.Transport);
    partial void OnShowSerialPortsChanged(bool value) => RaiseSettingChanged(nameof(ShowSerialPorts), value, ApplicationSettingChangeCategory.Persist | ApplicationSettingChangeCategory.PortVisibility);
    partial void OnShowVirtualPortsChanged(bool value) => RaiseSettingChanged(nameof(ShowVirtualPorts), value, ApplicationSettingChangeCategory.Persist | ApplicationSettingChangeCategory.PortVisibility);
    partial void OnPreventSleepChanged(bool value) => RaiseSettingChanged(nameof(PreventSleep), value, ApplicationSettingChangeCategory.Persist | ApplicationSettingChangeCategory.PreventSleep);
    partial void OnLogFileNameFormatChanged(string value) => RaiseSettingChanged(nameof(LogFileNameFormat), value, ApplicationSettingChangeCategory.Persist | ApplicationSettingChangeCategory.LogFileNamePreview);
    partial void OnPrivateMemoryMonitorEnabledChanged(bool value) => RaiseSettingChanged(nameof(PrivateMemoryMonitorEnabled), value, ApplicationSettingChangeCategory.Persist | ApplicationSettingChangeCategory.PrivateMemoryMonitor);

    partial void OnReceiveModeChanged(ReceiveDisplayMode value) => RaiseSettingChanged(nameof(ReceiveMode), value);
    partial void OnTimestampEnabledChanged(bool value) => RaiseSettingChanged(nameof(TimestampEnabled), value);
    partial void OnTimestampFormatChanged(string value) => RaiseSettingChanged(nameof(TimestampFormat), value);
    partial void OnLoggingEnabledChanged(bool value) => RaiseSettingChanged(nameof(LoggingEnabled), value);
    partial void OnLogDirectoryChanged(string value) => RaiseSettingChanged(nameof(LogDirectory), value);
    partial void OnDefaultSendModeChanged(SendMode value) => RaiseSettingChanged(nameof(DefaultSendMode), value);
    partial void OnDefaultNewlineChanged(NewlinePolicy value) => RaiseSettingChanged(nameof(DefaultNewline), value);
    partial void OnLogRotationMegabytesChanged(int value) => RaiseSettingChanged(nameof(LogRotationMegabytes), value);
    partial void OnLogRotationEnabledChanged(bool value) => RaiseSettingChanged(nameof(LogRotationEnabled), value);
    partial void OnDisplayBudgetMegabytesChanged(int value) => RaiseSettingChanged(nameof(DisplayBudgetMegabytes), value);
    partial void OnFreezeAfterSendChanged(bool value) => RaiseSettingChanged(nameof(FreezeAfterSend), value);
    partial void OnSendPrefixEnabledChanged(bool value) => RaiseSettingChanged(nameof(SendPrefixEnabled), value);
    partial void OnSendPrefixChanged(string value) => RaiseSettingChanged(nameof(SendPrefix), value);
    partial void OnPauseFollowOnMouseWheelChanged(bool value) => RaiseSettingChanged(nameof(PauseFollowOnMouseWheel), value);
    partial void OnPauseFollowOnFocusChanged(bool value) => RaiseSettingChanged(nameof(PauseFollowOnFocus), value);
    partial void OnShowPauseHintChanged(bool value) => RaiseSettingChanged(nameof(ShowPauseHint), value);
    partial void OnAutoBackupEnabledChanged(bool value) => RaiseSettingChanged(nameof(AutoBackupEnabled), value);
    partial void OnAutoBackupPeriodDaysChanged(int value) => RaiseSettingChanged(nameof(AutoBackupPeriodDays), value);
    partial void OnCloseToTaskbarChanged(bool value) => RaiseSettingChanged(nameof(CloseToTaskbar), value);
    partial void OnAutoCheckUpdatesChanged(bool value) => RaiseSettingChanged(nameof(AutoCheckUpdates), value);
    partial void OnWordWrapChanged(bool value) => RaiseSettingChanged(nameof(WordWrap), value);
    partial void OnShowLineNumbersChanged(bool value) => RaiseSettingChanged(nameof(ShowLineNumbers), value);
    partial void OnHighlightCurrentLineChanged(bool value) => RaiseSettingChanged(nameof(HighlightCurrentLine), value);
    partial void OnShowControlCharactersChanged(bool value) => RaiseSettingChanged(nameof(ShowControlCharacters), value);
    partial void OnShowSpacesChanged(bool value) => RaiseSettingChanged(nameof(ShowSpaces), value);
    partial void OnShowTabsChanged(bool value) => RaiseSettingChanged(nameof(ShowTabs), value);
    partial void OnShowCoverPageChanged(bool value) => RaiseSettingChanged(nameof(ShowCoverPage), value);
    partial void OnCoverPageAnimationEnabledChanged(bool value) => RaiseSettingChanged(nameof(CoverPageAnimationEnabled), value);
    partial void OnLogFontSizeChanged(double value) => RaiseSettingChanged(nameof(LogFontSize), value);
    partial void OnLogFontFamilyChanged(string value) => RaiseSettingChanged(nameof(LogFontFamily), value);
    partial void OnShowPortTypeChanged(bool value) => RaiseSettingChanged(nameof(ShowPortType), value);
    partial void OnSearchOpacityChanged(double value) => RaiseSettingChanged(nameof(SearchOpacity), value);
    partial void OnIsSidebarVisibleChanged(bool value) => RaiseSettingChanged(nameof(IsSidebarVisible), value);
    partial void OnIsBottomSendVisibleChanged(bool value) => RaiseSettingChanged(nameof(IsBottomSendVisible), value);
    partial void OnPortSortModeChanged(PortSortMode value) => RaiseSettingChanged(nameof(PortSortMode), value);
    partial void OnShowHiddenPortsChanged(bool value) => RaiseSettingChanged(nameof(ShowHiddenPorts), value);
    partial void OnTelnetPortChanged(int value) => RaiseSettingChanged(nameof(TelnetPort), value);
    partial void OnTelnetAllowRemoteChanged(bool value) => RaiseSettingChanged(nameof(TelnetAllowRemote), value);
    partial void OnTelnetAuthenticationEnabledChanged(bool value) => RaiseSettingChanged(nameof(TelnetAuthenticationEnabled), value);
    partial void OnTelnetUsernameChanged(string value) => RaiseSettingChanged(nameof(TelnetUsername), value);
    partial void OnSplitOrientationChanged(SplitLayoutOrientation value) => RaiseSettingChanged(nameof(SplitOrientation), value);
    partial void OnSplitterRatioChanged(double value) => RaiseSettingChanged(nameof(SplitterRatio), value);
    partial void OnPrivateMemoryThresholdMiBChanged(int value) => RaiseSettingChanged(nameof(PrivateMemoryThresholdMiB), value);
    partial void OnShowMemoryMonitorChanged(bool value) => RaiseSettingChanged(nameof(ShowMemoryMonitor), value);
    partial void OnMemoryRefreshIntervalChanged(int value) => RaiseSettingChanged(nameof(MemoryRefreshInterval), value);
    partial void OnSystemMemoryRefreshIntervalChanged(int value) => RaiseSettingChanged(nameof(SystemMemoryRefreshInterval), value);
}
