using System.IO.Ports;
using DuCom.Core.Parsing;
using DuCom.Core.Sending;
using DuCom.Services;

namespace DuCom.ViewModels;

public partial class MainViewModel
{
    partial void OnDataBitsChanged(int value)
    {
        MarkSettingsDirty();
        SchedulePortSettingsApply();
    }

    partial void OnStopBitsChanged(StopBits value)
    {
        MarkSettingsDirty();
        SchedulePortSettingsApply();
    }

    partial void OnParityChanged(Parity value)
    {
        MarkSettingsDirty();
        SchedulePortSettingsApply();
    }

    partial void OnHandshakeChanged(Handshake value)
    {
        MarkSettingsDirty();
        SchedulePortSettingsApply();
    }

    partial void OnEncodingNameChanged(string value)
    {
        MarkSettingsDirty();
        SchedulePortSettingsApply();
    }

    partial void OnReceiveModeChanged(ReceiveDisplayMode value) => MarkSettingsDirty();

    partial void OnTimestampEnabledChanged(bool value) => MarkSettingsDirty();

    partial void OnTimestampFormatChanged(string value) => MarkSettingsDirty();

    partial void OnLoggingEnabledChanged(bool value) => MarkSettingsDirty();

    partial void OnLogDirectoryChanged(string value)
    {
        MarkSettingsDirty();
    }

    partial void OnDefaultSendModeChanged(SendMode value) => MarkSettingsDirty();

    partial void OnDefaultNewlineChanged(NewlinePolicy value) => MarkSettingsDirty();

    partial void OnLogRotationMegabytesChanged(int value) => MarkSettingsDirty();
    partial void OnLogRotationEnabledChanged(bool value) => MarkSettingsDirty();
    partial void OnDisplayBudgetMegabytesChanged(int value) => MarkSettingsDirty();
    partial void OnLogFileNameFormatChanged(string value)
    {
        OnPropertyChanged(nameof(LogFileNamePreview));
        MarkSettingsDirty();
    }
    partial void OnFreezeAfterSendChanged(bool value) => MarkSettingsDirty();
    partial void OnSendPrefixEnabledChanged(bool value) => MarkSettingsDirty();
    partial void OnSendPrefixChanged(string value) => MarkSettingsDirty();
    partial void OnPauseFollowOnMouseWheelChanged(bool value) => MarkSettingsDirty();
    partial void OnPauseFollowOnFocusChanged(bool value) => MarkSettingsDirty();
    partial void OnShowPauseHintChanged(bool value) => MarkSettingsDirty();
    partial void OnAutoBackupEnabledChanged(bool value) => MarkSettingsDirty();
    partial void OnAutoBackupPeriodDaysChanged(int value) => MarkSettingsDirty();
    partial void OnPreventSleepChanged(bool value)
    {
        MarkSettingsDirty();
        SystemPowerService.SetPreventSleep(value);
    }
    partial void OnCloseToTaskbarChanged(bool value) => MarkSettingsDirty();

    partial void OnAutoCheckUpdatesChanged(bool value) => MarkSettingsDirty();

    partial void OnWordWrapChanged(bool value) => MarkSettingsDirty();

    partial void OnShowLineNumbersChanged(bool value) => MarkSettingsDirty();

    partial void OnHighlightCurrentLineChanged(bool value) => MarkSettingsDirty();

    partial void OnShowControlCharactersChanged(bool value) => MarkSettingsDirty();

    partial void OnShowSpacesChanged(bool value) => MarkSettingsDirty();

    partial void OnShowTabsChanged(bool value) => MarkSettingsDirty();

    partial void OnShowCoverPageChanged(bool value) => MarkSettingsDirty();

    partial void OnCoverPageAnimationEnabledChanged(bool value) => MarkSettingsDirty();

    partial void OnLogFontSizeChanged(double value) => MarkSettingsDirty();
    partial void OnLogFontFamilyChanged(string value) => MarkSettingsDirty();
    partial void OnShowPortTypeChanged(bool value) => MarkSettingsDirty();
    partial void OnSearchOpacityChanged(double value) => MarkSettingsDirty();
    partial void OnIsSidebarVisibleChanged(bool value) => MarkSettingsDirty();
    partial void OnIsBottomSendVisibleChanged(bool value) => MarkSettingsDirty();
    partial void OnPortSortModeChanged(PortSortMode value) => MarkSettingsDirty();
    partial void OnShowHiddenPortsChanged(bool value) => MarkSettingsDirty();

    partial void OnShowSerialPortsChanged(bool value)
    {
        MarkSettingsDirty();
        RebuildPortItems(SelectedPort);
    }

    partial void OnShowVirtualPortsChanged(bool value)
    {
        MarkSettingsDirty();
        RebuildPortItems(SelectedPort);
    }

    partial void OnTelnetPortChanged(int value) => MarkSettingsDirty();
    partial void OnTelnetAllowRemoteChanged(bool value) => MarkSettingsDirty();

    partial void OnTelnetAuthenticationEnabledChanged(bool value) => MarkSettingsDirty();

    partial void OnTelnetUsernameChanged(string value) => MarkSettingsDirty();

    partial void OnSplitOrientationChanged(SplitLayoutOrientation value) => MarkSettingsDirty();

    partial void OnSplitterRatioChanged(double value) => MarkSettingsDirty();
}
