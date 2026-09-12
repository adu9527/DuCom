using System.Windows;
using CommunityToolkit.Mvvm.Input;
using DuCom.Core.Ports;
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
        ConfigurationSnapshotIo.Export(dialog.FileName, snapshot);
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
            if (ConfigurationSnapshotIo.TryImport(dialog.FileName, out ConfigurationSnapshot? snapshot, out IReadOnlyList<string> skipped))
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
        Workspace.CapturePortOverrides(),
        [.. DuCom.Core.Persistence.PortVisibility.NormalizeHidden(_hiddenPorts)],
        [.. CommandTargetPortNames],
        [.. Workspace.RightSessions.Where(session => session.IsOpen).Select(session => session.PortName)],
        SplitOrientation,
        Math.Clamp(SplitterRatio, 0.2d, 0.8d),
        [.. Workspace.Sessions.Where(session => session.IsOpen).Select(session => session.PortName)],
        [.. Workspace.Sessions.Where(session => session.IsOpen).Select(session => session.PortName)],
        Workspace.SelectedSession is { IsOpen: true, IsInRightPane: false } ? Workspace.SelectedSession.PortName : null,
        Workspace.SelectedRightSession is { IsOpen: true, IsInRightPane: true } ? Workspace.SelectedRightSession.PortName : null,
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
            ConfigurationApplyPlan plan = ConfigurationSnapshotNormalizer.Normalize(
                snapshot,
                TimestampFormatOptions,
                LogFontFamilies);

            // Refill the baud-rate list before selecting the value: clearing the list after
            // the selection leaves the ComboBox empty (same ordering rule as restore-defaults).
            if (plan.CustomBaudRates is not null)
            {
                BaudRates.Clear();
                foreach (int value in plan.CustomBaudRates)
                {
                    BaudRates.Add(value);
                }
            }

            BaudRate = snapshot.BaudRate;
            EnsureBaudRatePresent(snapshot.BaudRate);
            DataBits = snapshot.DataBits;
            StopBits = snapshot.StopBits;
            Parity = snapshot.Parity;
            Handshake = snapshot.Handshake;
            EncodingName = snapshot.EncodingName;
            ReceiveMode = snapshot.ReceiveMode;
            TimestampEnabled = snapshot.TimestampEnabled;
            TimestampFormat = plan.TimestampFormat;
            LoggingEnabled = snapshot.LoggingEnabled;
            LogDirectory = snapshot.LogDirectory;
            DefaultSendMode = snapshot.SendMode;
            DefaultNewline = snapshot.Newline;
            SerialParameters.RefreshDefault(GetDefaultSerialSettings(), DefaultSendMode, DefaultNewline);
            LogRotationMegabytes = snapshot.LogRotationMegabytes;
            LogRotationEnabled = snapshot.LogRotationEnabled;
            DisplayBudgetMegabytes = snapshot.DisplayBudgetMegabytes;
            PrivateMemoryMonitorEnabled = snapshot.PrivateMemoryMonitorEnabled;
            PrivateMemoryThresholdMiB = plan.PrivateMemoryThresholdMiB;
            ShowMemoryMonitor = snapshot.ShowMemoryMonitor;
            MemoryRefreshInterval = plan.MemoryRefreshInterval;
            SystemMemoryRefreshInterval = plan.SystemMemoryRefreshInterval;
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
            LogFontSize = plan.LogFontSize;
            LogFontFamily = plan.LogFontFamily;
            ShowPortType = snapshot.ShowPortType;
            SearchOpacity = plan.SearchOpacity;
            IsSidebarVisible = snapshot.IsSidebarVisible;
            IsBottomSendVisible = snapshot.IsBottomSendVisible;
            PortSortMode = snapshot.PortSortMode;
            ShowHiddenPorts = snapshot.ShowHiddenPorts;
            ShowSerialPorts = snapshot.ShowSerialPorts;
            ShowVirtualPorts = snapshot.ShowVirtualPorts;
            ShowCoverPage = snapshot.ShowCoverPage;
            CoverPageAnimationEnabled = snapshot.CoverPageAnimationEnabled;
            TelnetPort = plan.TelnetPort;
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
            EnsureActiveBaudRatesPresent();
            Workspace.SetPersistedState(plan.PortOverrides, plan.RightPanePorts, plan.SessionOrder,
                plan.OpenSessionPorts, snapshot.SelectedSessionPort, snapshot.SelectedRightSessionPort);
            SplitOrientation = snapshot.SplitOrientation;
            SplitterRatio = plan.SplitterRatio;
            _hiddenPorts.Clear();
            foreach (string hidden in plan.HiddenPorts)
            {
                _hiddenPorts.Add(hidden);
            }
            Volatile.Write(ref _commandTargetPortNames, plan.CommandTargetPortNames);
        }
        finally
        {
            _isLoadingSettings = false;
        }
    }

}
