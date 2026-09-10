using System.Text;
using System.IO.Ports;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using DuCom.Core.Ports;
using DuCom.Core.Parsing;
using DuCom.Core.Sending;
using DuCom.Services;

namespace DuCom.ViewModels;

public partial class MainViewModel
{
    [RelayCommand]
    private void RestoreDefaultSettings()
    {
        if (!ThemedMessageDialog.Confirm(
            _settingsWindow ?? Application.Current.MainWindow,
            GetResourceString("Settings.RestoreDefaults.Confirmation"),
            GetResourceString("Settings.RestoreDefaults"),
            "Dialog.RestoreDefaults",
            "Dialog.Cancel"))
        {
            return;
        }

        _isLoadingSettings = true;
        try
        {
            BaudRate = 1_152_000;
            SerialParameterBaudRate = 1_152_000;
            RestoreDefaultBaudRates();
            OnPropertyChanged(nameof(BaudRate));
            OnPropertyChanged(nameof(SerialParameterBaudRate));
            DataBits = 8;
            StopBits = StopBits.One;
            Parity = Parity.None;
            Handshake = Handshake.None;
            EncodingName = Encoding.UTF8.WebName;
            ReceiveMode = ReceiveDisplayMode.Str;
            TimestampEnabled = true;
            TimestampFormat = "HH:mm:ss.fff";
            LoggingEnabled = true;
            LogDirectory = GetDefaultLogDirectory();
            DefaultSendMode = SendMode.Str;
            LogRotationMegabytes = 40;
            LogRotationEnabled = true;
            DisplayBudgetMegabytes = 64;
            PrivateMemoryMonitorEnabled = false;
            PrivateMemoryThresholdMiB = 1024;
            ShowMemoryMonitor = true;
            MemoryRefreshInterval = 2;
            SystemMemoryRefreshInterval = 5;
            LogFileNameFormat = "{Port}-{yyyy}-{MM}-{dd} {HH}-{mm}-{ss}.{fff}";
            FreezeAfterSend = false;
            SendPrefixEnabled = true;
            SendPrefix = "TX > ";
            ShowPortType = false;
            PauseFollowOnMouseWheel = true;
            PauseFollowOnFocus = false;
            ShowPauseHint = true;
            AutoBackupEnabled = true;
            AutoBackupPeriodDays = 7;
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
            LogFontSize = 14;
            LogFontFamily = "Cascadia Mono";
            TelnetPort = 23;
            TelnetAllowRemote = false;
        }
        finally
        {
            _isLoadingSettings = false;
        }

        SystemPowerService.SetPreventSleep(false);
        ApplyDefaultSettingsToEditor();
        SerialParameterSendMode = DefaultSendMode;
        SerialParameterNewline = DefaultNewline;
        _ = Application.Current.Dispatcher.BeginInvoke(
            () =>
            {
                OnPropertyChanged(nameof(SerialParameterBaudRate));
                foreach (SessionViewModel session in Sessions)
                {
                    session.RefreshBaudRateDisplay();
                }
            },
            System.Windows.Threading.DispatcherPriority.DataBind);
        SaveSettings();
    }

    [RelayCommand]
    private async Task RestoreTransportDefaultsAsync()
    {
        SessionViewModel? session = _portSettingsTargetSession;
        if (session is null)
        {
            RestoreDefaultBaudRates();
            _isLoadingSettings = true;
            try
            {
                BaudRate = 1_152_000;
                DataBits = 8;
                StopBits = StopBits.One;
                Parity = Parity.None;
                Handshake = Handshake.None;
                EncodingName = Encoding.UTF8.WebName;
            }
            finally
            {
                _isLoadingSettings = false;
            }

            ApplyDefaultSettingsToEditor();
            OnPropertyChanged(nameof(SerialParameterBaudRate));
            MarkSettingsDirty();
            return;
        }

        if (session.IsBusy)
        {
            return;
        }

        _portSettingsApplyTimer.Stop();
        _portSettingsApplyPending = false;
        RestoreDefaultBaudRates();
        SerialPortSettings current = session.WorkspaceSession.Settings;
        SerialPortSettings defaults = current with
        {
            BaudRate = 1_152_000,
            DataBits = 8,
            StopBits = StopBits.One,
            Parity = Parity.None,
            Handshake = Handshake.None,
            EncodingName = Encoding.UTF8.WebName,
            DtrEnable = false,
            RtsEnable = false,
            DiscardNull = false,
        };
        await ApplyPortSettingsAsync(session, defaults);
        if (session.WorkspaceSession.Settings != defaults)
        {
            return;
        }

        ApplySessionSettingsToEditor(defaults);
        OnPropertyChanged(nameof(SerialParameterBaudRate));
        RememberPortOverride(session.PortName);
    }

    [RelayCommand]
    private void RestoreReceiveAndLogDefaults()
    {
        ReceiveMode = ReceiveDisplayMode.Str;
        TimestampEnabled = true;
        TimestampFormat = "HH:mm:ss.fff";
        LoggingEnabled = true;
        LogRotationMegabytes = 40;
        LogRotationEnabled = true;
        DisplayBudgetMegabytes = 64;
        LogFileNameFormat = "{Port}-{yyyy}-{MM}-{dd} {HH}-{mm}-{ss}.{fff}";
        SendPrefixEnabled = true;
        SendPrefix = "TX > ";
    }

    [RelayCommand]
    private void RestoreSendDefaults()
    {
        DefaultSendMode = SendMode.Str;
        DefaultNewline = NewlinePolicy.None;

        _isLoadingSettings = true;
        try
        {
            SerialParameterSendMode = SendMode.Str;
            SerialParameterNewline = NewlinePolicy.None;
            SerialParameterInterpretSendEscapes = false;
            SerialParameterTimedSendEnabled = false;
            SerialParameterTimedSendIntervalMilliseconds = 1000;
        }
        finally
        {
            _isLoadingSettings = false;
        }
    }

    [RelayCommand]
    private void RestoreAppearanceDefaults()
    {
        SearchOpacity = 1d;
        LogFontSize = 14;
        LogFontFamily = "Cascadia Mono";
        ShowCoverPage = true;
        CoverPageAnimationEnabled = true;
    }

    [RelayCommand]
    private void ResetSearchOpacity() => SearchOpacity = 1d;

    internal void NotifyAutoScrollPaused()
    {
        if (ShowPauseHint)
        {
            StatusMessage = GetResourceString("Status.AutoScrollPaused");
        }
    }
}
