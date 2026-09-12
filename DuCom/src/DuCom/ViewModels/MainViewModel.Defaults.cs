using System.IO.Ports;
using System.Text;
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
            RestoreApplicationSettingDefaults();
            RestoreDefaultBaudRates();
            OnPropertyChanged(nameof(BaudRate));
        }
        finally
        {
            _isLoadingSettings = false;
        }

        SystemPowerService.SetPreventSleep(false);
        SerialParameters.BeginDefault(GetDefaultSerialSettings(), DefaultSendMode, DefaultNewline);
        _ = Application.Current.Dispatcher.BeginInvoke(
            () =>
            {
                SerialParameters.RefreshBaudRateBinding();
                foreach (SessionViewModel session in Workspace.Sessions)
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
        if (!SerialParameters.CanRestoreTransportDefaults)
        {
            return;
        }

        RestoreDefaultBaudRates();
        SerialPortSettings defaults = GetDefaultSerialSettings() with
        {
            BaudRate = SettingsCatalog.DefaultBaudRate,
            DataBits = SettingsCatalog.DefaultDataBits,
            StopBits = StopBits.One,
            Parity = Parity.None,
            Handshake = Handshake.None,
            EncodingName = Encoding.UTF8.WebName,
            DtrEnable = false,
            RtsEnable = false,
            DiscardNull = false,
        };
        if (!await SerialParameters.RestoreTransportDefaultsAsync(defaults))
        {
            return;
        }

        MarkSettingsDirty();
    }

    [RelayCommand]
    private void RestoreReceiveAndLogDefaults()
    {
        ReceiveMode = ReceiveDisplayMode.Str;
        TimestampEnabled = true;
        TimestampFormat = SettingsCatalog.DefaultTimestampFormat;
        LoggingEnabled = true;
        LogRotationMegabytes = SettingsCatalog.DefaultLogRotationMegabytes;
        LogRotationEnabled = true;
        DisplayBudgetMegabytes = SettingsCatalog.DefaultDisplayBudgetMegabytes;
        LogFileNameFormat = SettingsCatalog.DefaultLogFileNameFormat;
        SendPrefixEnabled = true;
        SendPrefix = "TX > ";
    }

    [RelayCommand]
    private void RestoreSendDefaults()
    {
        DefaultSendMode = SendMode.Str;
        DefaultNewline = NewlinePolicy.None;

        SerialParameters.LoadSendDefaults(SendMode.Str, NewlinePolicy.None);
    }

    [RelayCommand]
    private void RestoreAppearanceDefaults()
    {
        SearchOpacity = 1d;
        LogFontSize = SettingsCatalog.DefaultLogFontSize;
        LogFontFamily = SettingsCatalog.DefaultLogFontFamily;
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
