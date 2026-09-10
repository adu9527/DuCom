using System.IO;
using System.IO.Ports;
using System.Text.Json;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using DuCom.Core.Parsing;
using DuCom.Core.Persistence;
using DuCom.Core.Ports;
using DuCom.Core.Sending;
using DuCom.Services;
using Microsoft.Win32;

namespace DuCom.ViewModels;

public partial class MainViewModel
{
    [RelayCommand]
    private void ExportConfiguration()
    {
        SaveFileDialog dialog = new() { Filter = "DuCom settings (*.json)|*.json", FileName = "ducom-settings.json" };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        ConfigurationSnapshot snapshot = CaptureConfiguration();
        File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(snapshot, ConfigurationJsonOptions));
    }

    [RelayCommand]
    private void ImportConfiguration()
    {
        OpenFileDialog dialog = new() { Filter = "DuCom settings (*.json)|*.json" };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            string json = File.ReadAllText(dialog.FileName);
            if (TolerantJsonLoader.TryLoad<ConfigurationSnapshot>(json, ConfigurationJsonOptions, out ConfigurationSnapshot? snapshot, out IReadOnlyList<string> skipped))
            {
                if (skipped.Count > 0)
                {
                    Program.DiagnosticLog?.Warning($"Import skipped {skipped.Count} unreadable settings field(s): {string.Join(", ", skipped)}");
                }

                ApplyConfiguration(snapshot!);
            }
            else
            {
                Program.DiagnosticLog?.Warning($"Failed to import settings from {dialog.FileName}.");
            }
        }
        catch (Exception exception)
        {
            Program.DiagnosticLog?.Warning($"Failed to import settings from {dialog.FileName}.", exception);
        }
    }

    internal void OpenSettingsCategory(int category)
    {
        _ = ToggleSettingsAsync();
        _ = Application.Current.Dispatcher.BeginInvoke(
            () => _settingsWindow?.SelectCategory(category),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private ConfigurationSnapshot CaptureConfiguration() => new(
        BaudRate,
        DataBits,
        StopBits,
        Parity,
        Handshake,
        EncodingName,
        ReceiveMode,
        TimestampEnabled,
        LoggingEnabled,
        LogDirectory,
        DefaultSendMode,
        DefaultNewline,
        TimestampFormat,
        LogRotationMegabytes,
        LogRotationEnabled,
        DisplayBudgetMegabytes,
        PrivateMemoryMonitorEnabled,
        Math.Clamp(PrivateMemoryThresholdMiB, 1, 1_048_576),
        LogFileNameFormat,
        FreezeAfterSend,
        SendPrefixEnabled,
        SendPrefix,
        PauseFollowOnMouseWheel,
        PauseFollowOnFocus,
        ShowPauseHint,
        AutoBackupEnabled,
        AutoBackupPeriodDays,
        PreventSleep,
        CloseToTaskbar,
        WordWrap,
        ShowLineNumbers,
        HighlightCurrentLine,
        ShowControlCharacters,
        ShowSpaces,
        ShowTabs,
        LogFontSize,
        LogFontFamily,
        ((App)Application.Current).CurrentLanguage,
        ((App)Application.Current).CurrentThemeMode == "Light" ? "Light" : "Dark",
        ShowPortType,
        SearchOpacity,
        IsSidebarVisible,
        IsBottomSendVisible,
        PortSortMode,
        ShowHiddenPorts,
        ShowSerialPorts,
        ShowVirtualPorts,
        ShowCoverPage,
        CoverPageAnimationEnabled,
        TelnetPort,
        TelnetAllowRemote,
        TelnetAuthenticationEnabled,
        TelnetUsername,
        [.. BaudRates],
        CapturePortOverrides(),
        [.. DuCom.Core.Persistence.PortVisibility.NormalizeHidden(_hiddenPorts)],
        [.. CommandTargetPortNames],
        [.. RightSessions.Where(session => session.IsOpen).Select(session => session.PortName)],
        SplitOrientation,
        Math.Clamp(SplitterRatio, 0.2d, 0.8d),
        [.. Sessions.Where(session => session.IsOpen).Select(session => session.PortName)],
        [.. Sessions.Where(session => session.IsOpen).Select(session => session.PortName)],
        SelectedSession is { IsOpen: true, IsInRightPane: false } ? SelectedSession.PortName : null,
        SelectedRightSession is { IsOpen: true, IsInRightPane: true } ? SelectedRightSession.PortName : null,
        AutoCheckUpdates,
        SkippedUpdateVersion,
        ShowMemoryMonitor,
        Math.Clamp(MemoryRefreshInterval, 1, 60),
        Math.Clamp(SystemMemoryRefreshInterval, 1, 60),
        CurrentSettingsSchemaVersion);

    private void ApplyConfiguration(ConfigurationSnapshot snapshot)
    {
        _isLoadingSettings = true;
        try
        {
            // Refill the baud-rate list before selecting the value: clearing the list after
            // the selection leaves the ComboBox empty (same ordering rule as restore-defaults).
            if (snapshot.CustomBaudRates is { Count: > 0 })
            {
                BaudRates.Clear();
                foreach (int value in snapshot.CustomBaudRates.Where(value => value > 0).Distinct().Order())
                {
                    BaudRates.Add(value);
                }
            }

            BaudRate = snapshot.BaudRate;
            SerialParameterBaudRate = snapshot.BaudRate;
            EnsureBaudRatePresent(snapshot.BaudRate);
            DataBits = snapshot.DataBits;
            StopBits = snapshot.StopBits;
            Parity = snapshot.Parity;
            Handshake = snapshot.Handshake;
            EncodingName = snapshot.EncodingName;
            ReceiveMode = snapshot.ReceiveMode;
            TimestampEnabled = snapshot.TimestampEnabled;
            TimestampFormat = TimestampFormatOptions.Contains(snapshot.TimestampFormat)
                ? snapshot.TimestampFormat
                : "HH:mm:ss.fff";
            LoggingEnabled = snapshot.LoggingEnabled;
            LogDirectory = snapshot.LogDirectory;
            DefaultSendMode = snapshot.SendMode;
            DefaultNewline = snapshot.Newline;
            LogRotationMegabytes = snapshot.LogRotationMegabytes;
            LogRotationEnabled = snapshot.LogRotationEnabled;
            DisplayBudgetMegabytes = snapshot.DisplayBudgetMegabytes;
            PrivateMemoryMonitorEnabled = snapshot.PrivateMemoryMonitorEnabled;
            PrivateMemoryThresholdMiB = Math.Clamp(snapshot.PrivateMemoryThresholdMiB, 1, 1_048_576);
            ShowMemoryMonitor = snapshot.ShowMemoryMonitor;
            MemoryRefreshInterval = Math.Clamp(snapshot.MemoryRefreshInterval, 1, 60);
            SystemMemoryRefreshInterval = Math.Clamp(snapshot.SystemMemoryRefreshInterval, 1, 60);
            LogFileNameFormat = snapshot.LogFileNameFormat;
            FreezeAfterSend = snapshot.FreezeAfterSend;
            SendPrefixEnabled = snapshot.SendPrefixEnabled;
            SendPrefix = snapshot.SendPrefix;
            PauseFollowOnMouseWheel = snapshot.PauseFollowOnMouseWheel;
            PauseFollowOnFocus = snapshot.PauseFollowOnFocus;
            ShowPauseHint = snapshot.ShowPauseHint;
            AutoBackupEnabled = snapshot.AutoBackupEnabled;
            AutoBackupPeriodDays = snapshot.AutoBackupPeriodDays;
            PreventSleep = snapshot.PreventSleep;
            CloseToTaskbar = snapshot.CloseToTaskbar;
            WordWrap = snapshot.WordWrap;
            ShowLineNumbers = snapshot.ShowLineNumbers;
            HighlightCurrentLine = snapshot.HighlightCurrentLine;
            ShowControlCharacters = snapshot.ShowControlCharacters;
            ShowSpaces = snapshot.ShowSpaces;
            ShowTabs = snapshot.ShowTabs;
            LogFontSize = snapshot.LogFontSize == 12 ? 14 : snapshot.LogFontSize;
            LogFontFamily = LogFontFamilies.Contains(snapshot.LogFontFamily, StringComparer.OrdinalIgnoreCase)
                ? snapshot.LogFontFamily
                : "Cascadia Mono";
            ShowPortType = snapshot.ShowPortType;
            SearchOpacity = Math.Clamp(snapshot.SearchOpacity, 0.2d, 1d);
            IsSidebarVisible = snapshot.IsSidebarVisible;
            IsBottomSendVisible = snapshot.IsBottomSendVisible;
            PortSortMode = snapshot.PortSortMode;
            ShowHiddenPorts = snapshot.ShowHiddenPorts;
            ShowSerialPorts = snapshot.ShowSerialPorts;
            ShowVirtualPorts = snapshot.ShowVirtualPorts;
            ShowCoverPage = snapshot.ShowCoverPage;
            CoverPageAnimationEnabled = snapshot.CoverPageAnimationEnabled;
            TelnetPort = Math.Clamp(snapshot.TelnetPort, 1, 65_535);
            TelnetAllowRemote = snapshot.TelnetAllowRemote;
            TelnetAuthenticationEnabled = snapshot.TelnetAuthenticationEnabled;
            TelnetUsername = snapshot.TelnetUsername ?? string.Empty;
            AutoCheckUpdates = snapshot.AutoCheckUpdates;
            SkippedUpdateVersion = snapshot.SkippedUpdateVersion;
            if (!string.IsNullOrWhiteSpace(snapshot.Language))
            {
                ((App)Application.Current).ApplyLanguage(snapshot.Language);
            }

            if (!((App)Application.Current).IsThemeSpecifiedOnCommandLine)
            {
                ((App)Application.Current).ApplyTheme(snapshot.ThemeMode);
            }

            SystemPowerService.SetPreventSleep(PreventSleep);
            _portOverrides = new Dictionary<string, PortSettingSnapshot>(
                snapshot.PortOverrides ?? [],
                StringComparer.OrdinalIgnoreCase);
            EnsureActiveBaudRatesPresent();
            _persistedRightPanePorts = [.. snapshot.RightPanePorts ?? []];
            _persistedSessionOrder = [.. snapshot.SessionOrder ?? []];
            _persistedOpenSessionPorts = [.. snapshot.OpenSessionPorts ?? []];
            _persistedSelectedSessionPort = snapshot.SelectedSessionPort;
            _persistedSelectedRightSessionPort = snapshot.SelectedRightSessionPort;
            SplitOrientation = snapshot.SplitOrientation;
            SplitterRatio = Math.Clamp(snapshot.SplitterRatio, 0.2d, 0.8d);
            _hiddenPorts.Clear();
            foreach (string hidden in DuCom.Core.Persistence.PortVisibility.NormalizeHidden(snapshot.HiddenPorts))
            {
                _hiddenPorts.Add(hidden);
            }
            Volatile.Write(ref _commandTargetPortNames, NormalizePortNames(snapshot.CommandTargetPortNames));
        }
        finally
        {
            _isLoadingSettings = false;
        }
    }

    private Dictionary<string, PortSettingSnapshot> CapturePortOverrides()
    {
        Dictionary<string, PortSettingSnapshot> result = new(_portOverrides, StringComparer.OrdinalIgnoreCase);
        foreach (SessionViewModel session in Sessions)
        {
            SerialPortSettings settings = session.WorkspaceSession.Settings;
            result[session.PortName] = new PortSettingSnapshot(
                settings.BaudRate,
                settings.DataBits,
                settings.StopBits,
                settings.Parity,
                settings.Handshake,
                settings.EncodingName,
                settings.DtrEnable,
                settings.RtsEnable,
                settings.DiscardNull,
                session.SendMode,
                session.Newline,
                session.ReceiveMode,
                session.TimestampEnabled,
                session.LoggingEnabled,
                LogDirectory,
                LogRotationMegabytes,
                LogRotationEnabled,
                DisplayBudgetMegabytes,
                LogFileNameFormat,
                SendPrefixEnabled,
                SendPrefix,
                FollowEnd: session.FollowEnd,
                FilterEnabled: session.FilterEnabled,
                AutoReconnect: session.AutoReconnect,
                HighlightRuleProjectId: session.HighlightRuleProjectId,
                HighlightRuleChoiceMade: session.HighlightRuleProjectId is null);
        }

        return result;
    }

    private sealed record ConfigurationSnapshot(
        int BaudRate,
        int DataBits,
        StopBits StopBits,
        Parity Parity,
        Handshake Handshake,
        string EncodingName,
        ReceiveDisplayMode ReceiveMode,
        bool TimestampEnabled,
        bool LoggingEnabled,
        string LogDirectory,
        SendMode SendMode,
        NewlinePolicy Newline,
        string TimestampFormat = "HH:mm:ss.fff",
        int LogRotationMegabytes = 40,
        bool LogRotationEnabled = true,
        int DisplayBudgetMegabytes = 64,
        bool PrivateMemoryMonitorEnabled = false,
        int PrivateMemoryThresholdMiB = 1024,
        string LogFileNameFormat = "{Port}-{yyyy}-{MM}-{dd} {HH}-{mm}-{ss}.{fff}",
        bool FreezeAfterSend = false,
        bool SendPrefixEnabled = true,
        string SendPrefix = "TX > ",
        bool PauseFollowOnMouseWheel = true,
        bool PauseFollowOnFocus = false,
        bool ShowPauseHint = true,
        bool AutoBackupEnabled = true,
        int AutoBackupPeriodDays = 7,
        bool PreventSleep = false,
        bool CloseToTaskbar = false,
        bool WordWrap = false,
        bool ShowLineNumbers = true,
        bool HighlightCurrentLine = true,
        bool ShowControlCharacters = false,
        bool ShowSpaces = false,
        bool ShowTabs = false,
        double LogFontSize = 14,
        string LogFontFamily = "Cascadia Mono",
        string Language = "",
        string ThemeMode = "Dark",
        bool ShowPortType = false,
        double SearchOpacity = 1d,
        bool IsSidebarVisible = true,
        bool IsBottomSendVisible = true,
        PortSortMode PortSortMode = PortSortMode.NameAscending,
        bool ShowHiddenPorts = false,
        bool ShowSerialPorts = true,
        bool ShowVirtualPorts = true,
        bool ShowCoverPage = true,
        bool CoverPageAnimationEnabled = true,
        int TelnetPort = 23,
        bool TelnetAllowRemote = false,
        bool TelnetAuthenticationEnabled = false,
        string? TelnetUsername = null,
        List<int>? CustomBaudRates = null,
        Dictionary<string, PortSettingSnapshot>? PortOverrides = null,
        List<string>? HiddenPorts = null,
        List<string>? CommandTargetPortNames = null,
        List<string>? RightPanePorts = null,
        SplitLayoutOrientation SplitOrientation = SplitLayoutOrientation.Vertical,
        double SplitterRatio = 0.5d,
        List<string>? SessionOrder = null,
        List<string>? OpenSessionPorts = null,
        string? SelectedSessionPort = null,
        string? SelectedRightSessionPort = null,
        bool AutoCheckUpdates = true,
        string? SkippedUpdateVersion = null,
        bool ShowMemoryMonitor = true,
        int MemoryRefreshInterval = 2,
        int SystemMemoryRefreshInterval = 5,
        int SchemaVersion = CurrentSettingsSchemaVersion);

    private static string[] NormalizePortNames(IEnumerable<string>? portNames) => [.. (portNames ?? [])
        .Where(name => !string.IsNullOrWhiteSpace(name))
        .Select(name => name.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
        .ThenBy(name => name, StringComparer.Ordinal)];

    private sealed record PortSettingSnapshot(
        int BaudRate,
        int DataBits,
        StopBits StopBits,
        Parity Parity,
        Handshake Handshake,
        string EncodingName,
        bool? DtrEnable = null,
        bool? RtsEnable = null,
        bool? DiscardNull = null,
        SendMode? SendMode = null,
        NewlinePolicy? Newline = null,
        ReceiveDisplayMode? ReceiveMode = null,
        bool? TimestampEnabled = null,
        bool? LoggingEnabled = null,
        string? LogDirectory = null,
        int? LogRotationMegabytes = null,
        bool? LogRotationEnabled = null,
        int? DisplayBudgetMegabytes = null,
        string? LogFileNameFormat = null,
        bool? SendPrefixEnabled = null,
        string? SendPrefix = null,
        bool? FollowEnd = null,
        bool? FilterEnabled = null,
        bool? AutoReconnect = null,
        Guid? HighlightRuleProjectId = null,
        bool HighlightRuleChoiceMade = false);

    private sealed record PortSessionPreferences(
        ReceiveDisplayMode ReceiveMode,
        bool TimestampEnabled,
        bool LoggingEnabled,
        string LogDirectory,
        long LogRotationBytes,
        bool LogRotationEnabled,
        int DisplayBudgetBytes,
        string LogFileNameFormat,
        bool SendPrefixEnabled,
        string SendPrefix,
        string TimestampFormat,
        bool FollowEnd,
        bool FilterEnabled,
        SendMode SendMode,
        NewlinePolicy Newline,
        Guid? HighlightRuleProjectId);
}
