using System.IO.Ports;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using DuCom.Core.Parsing;
using DuCom.Core.Ports;
using DuCom.Core.Sending;

namespace DuCom.ViewModels;

public partial class MainViewModel
{
    public bool IsSerialParametersEditable => _portSettingsTargetSession is { IsBusy: false };

    public bool IsEditingPortSettings => _portSettingsTargetSession is not null;

    [ObservableProperty]
    public partial SendMode SerialParameterSendMode { get; set; } = SendMode.Str;

    [ObservableProperty]
    public partial NewlinePolicy SerialParameterNewline { get; set; } = NewlinePolicy.None;

    [ObservableProperty]
    public partial bool SerialParameterInterpretSendEscapes { get; set; }

    [ObservableProperty]
    public partial bool SerialParameterTimedSendEnabled { get; set; }

    [ObservableProperty]
    public partial int SerialParameterTimedSendIntervalMilliseconds { get; set; } = 1000;

    [ObservableProperty]
    public partial int SerialParameterBaudRate { get; set; } = 1_152_000;

    [ObservableProperty]
    public partial int SerialParameterDataBits { get; set; } = 8;

    [ObservableProperty]
    public partial StopBits SerialParameterStopBits { get; set; } = StopBits.One;

    [ObservableProperty]
    public partial Parity SerialParameterParity { get; set; } = Parity.None;

    [ObservableProperty]
    public partial Handshake SerialParameterHandshake { get; set; } = Handshake.None;

    [ObservableProperty]
    public partial string SerialParameterEncodingName { get; set; } = Encoding.UTF8.WebName;

    [ObservableProperty]
    public partial bool SerialParameterDtrEnable { get; set; }

    [ObservableProperty]
    public partial bool SerialParameterRtsEnable { get; set; }

    [ObservableProperty]
    public partial bool SerialParameterDiscardNull { get; set; }

    [ObservableProperty]
    public partial bool SerialParameterAutoReconnect { get; set; }

    [ObservableProperty]
    public partial ReceiveDisplayMode SerialParameterReceiveMode { get; set; } = ReceiveDisplayMode.Str;

    [ObservableProperty]
    public partial bool SerialParameterTimestampEnabled { get; set; } = true;

    [ObservableProperty]
    public partial bool SerialParameterLoggingEnabled { get; set; } = true;

    [ObservableProperty]
    public partial bool SerialParameterFollowEnd { get; set; } = true;

    [ObservableProperty]
    public partial bool SerialParameterFilterEnabled { get; set; } = true;

    private void ApplySessionSettingsToEditor(SerialPortSettings settings)
    {
        _isLoadingSettings = true;
        try
        {
            SerialParameterBaudRate = settings.BaudRate;
            SerialParameterDataBits = settings.DataBits;
            SerialParameterStopBits = settings.StopBits;
            SerialParameterParity = settings.Parity;
            SerialParameterHandshake = settings.Handshake;
            SerialParameterEncodingName = settings.EncodingName;
            SerialParameterDtrEnable = settings.DtrEnable;
            SerialParameterRtsEnable = settings.RtsEnable;
            SerialParameterDiscardNull = settings.DiscardNull;
        }
        finally
        {
            _isLoadingSettings = false;
        }
    }

    private void ApplyDefaultSettingsToEditor()
    {
        _isLoadingSettings = true;
        try
        {
            SerialParameterBaudRate = BaudRate;
            SerialParameterDataBits = DataBits;
            SerialParameterStopBits = StopBits;
            SerialParameterParity = Parity;
            SerialParameterHandshake = Handshake;
            SerialParameterEncodingName = EncodingName;
            SerialParameterDtrEnable = false;
            SerialParameterRtsEnable = false;
            SerialParameterDiscardNull = false;
            SerialParameterAutoReconnect = false;
        }
        finally
        {
            _isLoadingSettings = false;
        }
    }

    private async void OnPortSettingsApplyTick(object? sender, EventArgs e)
    {
        _portSettingsApplyTimer.Stop();
        SessionViewModel? session = _portSettingsTargetSession;
        if (_isLoadingSettings || session is null)
        {
            return;
        }

        if (session.IsBusy)
        {
            _portSettingsApplyTimer.Start();
            return;
        }

        await ApplyPendingPortSettingsAsync(session);
    }

    private async Task ApplyPortSettingsAsync(SessionViewModel session, SerialPortSettings updated)
    {
        try
        {
            await session.ApplySettingsAsync(updated);
            RememberPortOverride(session.PortName, updated);
            StatusMessage = string.Empty;
            Program.DiagnosticLog?.Information($"Runtime serial settings updated. Port={session.PortName}; Baud={updated.BaudRate}; DataBits={updated.DataBits}; StopBits={updated.StopBits}; Parity={updated.Parity}; Handshake={updated.Handshake}; Encoding={updated.EncodingName}; DTR={updated.DtrEnable}; RTS={updated.RtsEnable}; DiscardNull={updated.DiscardNull}");
        }
        catch (Exception exception)
        {
            StatusMessage = GetResourceString("Status.InvalidPortSettings");
            Program.DiagnosticLog?.Error("Runtime serial settings update failed.", exception);
        }
    }

    private void SchedulePortSettingsApply()
    {
        SessionViewModel? session = _portSettingsTargetSession;
        if (_isLoadingSettings || session is null)
        {
            return;
        }

        _portSettingsApplyPending = true;
        _portSettingsApplyTimer.Stop();
        _portSettingsApplyTimer.Start();
    }

    private async Task<bool> FlushPortSettingsAsync()
    {
        _portSettingsApplyTimer.Stop();
        SessionViewModel? session = _portSettingsTargetSession;
        if (!_portSettingsApplyPending || session is null)
        {
            return true;
        }

        if (session.IsBusy)
        {
            StatusMessage = GetResourceString("Status.PortSettingsBusy");
            return false;
        }

        await ApplyPendingPortSettingsAsync(session);
        return !_portSettingsApplyPending;
    }

    private async Task ApplyPendingPortSettingsAsync(SessionViewModel session)
    {
        SerialPortSettings current = session.WorkspaceSession.Settings;
        SerialPortSettings updated = current with
        {
            BaudRate = SerialParameterBaudRate,
            DataBits = SerialParameterDataBits,
            StopBits = SerialParameterStopBits,
            Parity = SerialParameterParity,
            Handshake = SerialParameterHandshake,
            EncodingName = SerialParameterEncodingName,
            DtrEnable = SerialParameterDtrEnable,
            RtsEnable = SerialParameterRtsEnable,
            DiscardNull = SerialParameterDiscardNull,
        };
        _portSettingsApplyPending = false;
        await ApplyPortSettingsAsync(session, updated);
        _portSettingsApplyPending = session.WorkspaceSession.Settings != updated;
    }

    partial void OnSerialParameterSendModeChanged(SendMode value)
    {
        if (_portSettingsTargetSession is not null)
        {
            _portSettingsTargetSession.SendMode = value;
            RememberPortOverride(_portSettingsTargetSession.PortName);
        }
        else if (!_isLoadingSettings)
        {
            DefaultSendMode = value;
        }
    }

    partial void OnSerialParameterNewlineChanged(NewlinePolicy value)
    {
        if (_portSettingsTargetSession is not null)
        {
            _portSettingsTargetSession.Newline = value;
            RememberPortOverride(_portSettingsTargetSession.PortName);
        }
        else if (!_isLoadingSettings)
        {
            DefaultNewline = value;
        }
    }

    partial void OnSerialParameterInterpretSendEscapesChanged(bool value)
    {
        if (_portSettingsTargetSession is not null)
        {
            _portSettingsTargetSession.InterpretSendEscapes = value;
        }
    }

    partial void OnSerialParameterTimedSendEnabledChanged(bool value)
    {
        if (_portSettingsTargetSession is not null)
        {
            _portSettingsTargetSession.TimedSendEnabled = value;
        }
    }

    partial void OnSerialParameterTimedSendIntervalMillisecondsChanged(int value)
    {
        if (_portSettingsTargetSession is not null)
        {
            _portSettingsTargetSession.TimedSendIntervalMilliseconds = Math.Clamp(value, 50, 86_400_000);
        }
    }

    partial void OnSerialParameterReceiveModeChanged(ReceiveDisplayMode value) => ApplySessionPreferenceChange(session => session.ReceiveMode = value);
    partial void OnSerialParameterTimestampEnabledChanged(bool value) => ApplySessionPreferenceChange(session => session.TimestampEnabled = value);
    partial void OnSerialParameterLoggingEnabledChanged(bool value) => ApplySessionPreferenceChange(session => session.LoggingEnabled = value);
    partial void OnSerialParameterFollowEndChanged(bool value) => ApplySessionPreferenceChange(session => session.FollowEnd = value);
    partial void OnSerialParameterFilterEnabledChanged(bool value) => ApplySessionPreferenceChange(session => session.FilterEnabled = value);

    private void ApplySessionPreferenceChange(Action<SessionViewModel> update)
    {
        if (_isLoadingSettings || _portSettingsTargetSession is null)
        {
            return;
        }

        update(_portSettingsTargetSession);
        RememberPortOverride(_portSettingsTargetSession.PortName);
        if (_portSettingsTargetSession.IsOpen)
        {
            StatusMessage = GetResourceString("Status.SessionSettingRequiresReopen");
        }
    }

    partial void OnSerialParameterBaudRateChanged(int value) => ApplySerialParameterChange(() => BaudRate = value);
    partial void OnSerialParameterDataBitsChanged(int value) => ApplySerialParameterChange(() => DataBits = value);
    partial void OnSerialParameterStopBitsChanged(StopBits value) => ApplySerialParameterChange(() => StopBits = value);
    partial void OnSerialParameterParityChanged(Parity value) => ApplySerialParameterChange(() => Parity = value);
    partial void OnSerialParameterHandshakeChanged(Handshake value) => ApplySerialParameterChange(() => Handshake = value);
    partial void OnSerialParameterEncodingNameChanged(string value) => ApplySerialParameterChange(() => EncodingName = value);
    partial void OnSerialParameterDtrEnableChanged(bool value) => ApplySerialParameterChange(null);
    partial void OnSerialParameterRtsEnableChanged(bool value) => ApplySerialParameterChange(null);
    partial void OnSerialParameterDiscardNullChanged(bool value) => ApplySerialParameterChange(null);

    partial void OnSerialParameterAutoReconnectChanged(bool value)
    {
        if (_portSettingsTargetSession is not null)
        {
            _portSettingsTargetSession.AutoReconnect = value;
            RememberPortOverride(_portSettingsTargetSession.PortName);
        }
    }

    private void ApplySerialParameterChange(Action? updateDefault)
    {
        if (_isLoadingSettings)
        {
            return;
        }

        if (_portSettingsTargetSession is null)
        {
            updateDefault?.Invoke();
            return;
        }

        SchedulePortSettingsApply();
    }
}
