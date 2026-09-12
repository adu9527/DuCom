using System.ComponentModel;
using System.IO.Ports;
using System.Text;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DuCom.Core.Parsing;
using DuCom.Core.Ports;
using DuCom.Core.Sending;

namespace DuCom.ViewModels;

internal interface ISerialParametersSession : INotifyPropertyChanged
{
    string PortName { get; }
    SerialPortSettings Settings { get; }
    bool IsBusy { get; }
    bool IsOpen { get; }
    SendMode SendMode { get; set; }
    NewlinePolicy Newline { get; set; }
    bool InterpretSendEscapes { get; set; }
    bool TimedSendEnabled { get; set; }
    int TimedSendIntervalMilliseconds { get; set; }
    bool AutoReconnect { get; set; }
    ReceiveDisplayMode ReceiveMode { get; set; }
    bool TimestampEnabled { get; set; }
    bool LoggingEnabled { get; set; }
    bool FollowEnd { get; set; }
    bool FilterEnabled { get; set; }
    Task ApplySettingsAsync(SerialPortSettings settings, CancellationToken cancellationToken = default);
}

internal interface ISerialParametersApplyScheduler : IDisposable
{
    event EventHandler? Tick;
    void Schedule();
    void Cancel();
}

internal sealed class DispatcherSerialParametersApplyScheduler : ISerialParametersApplyScheduler
{
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Background)
    {
        Interval = TimeSpan.FromMilliseconds(250),
    };

    public DispatcherSerialParametersApplyScheduler() => _timer.Tick += OnTick;

    public event EventHandler? Tick;

    public void Schedule()
    {
        _timer.Stop();
        _timer.Start();
    }

    public void Cancel() => _timer.Stop();

    public void Dispose()
    {
        _timer.Stop();
        _timer.Tick -= OnTick;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _timer.Stop();
        Tick?.Invoke(this, EventArgs.Empty);
    }
}

internal sealed record SerialParametersEditorCallbacks(
    Func<SerialPortSettings> GetDefaultSettings,
    Action<SerialPortSettings> UpdateDefaultSettings,
    Action<SendMode> UpdateDefaultSendMode,
    Action<NewlinePolicy> UpdateDefaultNewline,
    Func<ISerialParametersSession?> GetActiveSession,
    Action<ISerialParametersSession, SerialPortSettings?> RememberPortOverride,
    Action<string> SetStatusResource,
    Action<string, Exception?> Log);

public partial class SerialParametersEditorViewModel : ObservableObject, IDisposable
{
    private readonly SerialParametersEditorCallbacks _callbacks;
    private readonly ISerialParametersApplyScheduler _scheduler;
    private ISerialParametersSession? _targetSession;
    private bool _isLoading;
    private bool _transportApplyPending;
    private long _transportChangeVersion;
    private long _targetGeneration;
    private readonly SemaphoreSlim _applyGate = new(1, 1);
    private Task _transportApplyTask = Task.CompletedTask;
    private bool _disposed;

    internal SerialParametersEditorViewModel(
        SerialParametersEditorCallbacks callbacks,
        ISerialParametersApplyScheduler? scheduler = null)
    {
        _callbacks = callbacks ?? throw new ArgumentNullException(nameof(callbacks));
        _scheduler = scheduler ?? new DispatcherSerialParametersApplyScheduler();
        _scheduler.Tick += OnApplyTick;
    }

    public bool IsSerialParametersEditable => _targetSession is { IsBusy: false };
    public bool IsEditingPortSettings => _targetSession is not null;
    internal ISerialParametersSession? TargetSession => _targetSession;
    internal bool HasPendingTransportChanges => _transportApplyPending;
    internal bool CanRestoreTransportDefaults => _targetSession is not { IsBusy: true };

    [ObservableProperty] public partial SendMode SerialParameterSendMode { get; set; } = SendMode.Str;
    [ObservableProperty] public partial NewlinePolicy SerialParameterNewline { get; set; } = NewlinePolicy.None;
    [ObservableProperty] public partial bool SerialParameterInterpretSendEscapes { get; set; }
    [ObservableProperty] public partial bool SerialParameterTimedSendEnabled { get; set; }
    [ObservableProperty] public partial int SerialParameterTimedSendIntervalMilliseconds { get; set; } = 1000;
    [ObservableProperty] public partial int SerialParameterBaudRate { get; set; } = 1_152_000;
    [ObservableProperty] public partial int SerialParameterDataBits { get; set; } = 8;
    [ObservableProperty] public partial StopBits SerialParameterStopBits { get; set; } = StopBits.One;
    [ObservableProperty] public partial Parity SerialParameterParity { get; set; } = Parity.None;
    [ObservableProperty] public partial Handshake SerialParameterHandshake { get; set; } = Handshake.None;
    [ObservableProperty] public partial string SerialParameterEncodingName { get; set; } = Encoding.UTF8.WebName;
    [ObservableProperty] public partial bool SerialParameterDtrEnable { get; set; }
    [ObservableProperty] public partial bool SerialParameterRtsEnable { get; set; }
    [ObservableProperty] public partial bool SerialParameterDiscardNull { get; set; }
    [ObservableProperty] public partial bool SerialParameterAutoReconnect { get; set; }
    [ObservableProperty] public partial ReceiveDisplayMode SerialParameterReceiveMode { get; set; } = ReceiveDisplayMode.Str;
    [ObservableProperty] public partial bool SerialParameterTimestampEnabled { get; set; } = true;
    [ObservableProperty] public partial bool SerialParameterLoggingEnabled { get; set; } = true;
    [ObservableProperty] public partial bool SerialParameterFollowEnd { get; set; } = true;
    [ObservableProperty] public partial bool SerialParameterFilterEnabled { get; set; } = true;

    public string ActiveEncodingName => _targetSession is not null
        ? _targetSession.Settings.EncodingName
        : _callbacks.GetActiveSession()?.Settings.EncodingName ?? _callbacks.GetDefaultSettings().EncodingName;

    internal void BeginDefault(SerialPortSettings settings, SendMode sendMode, NewlinePolicy newline)
    {
        End();
        RefreshDefault(settings, sendMode, newline);
    }

    internal void RefreshDefault(SerialPortSettings settings, SendMode sendMode, NewlinePolicy newline)
    {
        if (_targetSession is not null)
        {
            return;
        }

        LoadTransport(settings, includeSessionOnlyValues: false);
        Load(() =>
        {
            SerialParameterSendMode = sendMode;
            SerialParameterNewline = newline;
        });
    }

    internal void BeginSession(ISerialParametersSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        End();
        _targetSession = session;
        _targetSession.PropertyChanged += OnTargetSessionPropertyChanged;
        LoadTransport(session.Settings, includeSessionOnlyValues: true);
        Load(() =>
        {
            SerialParameterReceiveMode = session.ReceiveMode;
            SerialParameterTimestampEnabled = session.TimestampEnabled;
            SerialParameterLoggingEnabled = session.LoggingEnabled;
            SerialParameterFollowEnd = session.FollowEnd;
            SerialParameterFilterEnabled = session.FilterEnabled;
            SerialParameterSendMode = session.SendMode;
            SerialParameterNewline = session.Newline;
            SerialParameterInterpretSendEscapes = session.InterpretSendEscapes;
            SerialParameterTimedSendEnabled = session.TimedSendEnabled;
            SerialParameterTimedSendIntervalMilliseconds = session.TimedSendIntervalMilliseconds;
            SerialParameterAutoReconnect = session.AutoReconnect;
        });
        OnPropertyChanged(nameof(IsEditingPortSettings));
        OnPropertyChanged(nameof(IsSerialParametersEditable));
        OnPropertyChanged(nameof(ActiveEncodingName));
    }

    internal void End()
    {
        _scheduler.Cancel();
        _transportApplyPending = false;
        _targetGeneration++;
        if (_targetSession is not null)
        {
            _targetSession.PropertyChanged -= OnTargetSessionPropertyChanged;
            _targetSession = null;
            OnPropertyChanged(nameof(IsEditingPortSettings));
            OnPropertyChanged(nameof(IsSerialParametersEditable));
            OnPropertyChanged(nameof(ActiveEncodingName));
        }
    }

    internal void NotifyActiveSessionChanged() => OnPropertyChanged(nameof(ActiveEncodingName));

    internal void RefreshBaudRateBinding() => OnPropertyChanged(nameof(SerialParameterBaudRate));

    internal void SignalTransportChange() => ScheduleTransportApply();

    internal void LoadSendDefaults(SendMode sendMode, NewlinePolicy newline)
    {
        Load(() =>
        {
            SerialParameterSendMode = sendMode;
            SerialParameterNewline = newline;
            SerialParameterInterpretSendEscapes = false;
            SerialParameterTimedSendEnabled = false;
            SerialParameterTimedSendIntervalMilliseconds = 1000;
        });
    }

    internal async Task<bool> RestoreTransportDefaultsAsync(SerialPortSettings defaults)
    {
        ISerialParametersSession? session = _targetSession;
        if (session is null)
        {
            _callbacks.UpdateDefaultSettings(defaults);
            LoadTransport(defaults, includeSessionOnlyValues: false);
            return true;
        }

        if (session.IsBusy)
        {
            return false;
        }

        _scheduler.Cancel();
        _transportApplyPending = false;
        SerialPortSettings updated = session.Settings with
        {
            BaudRate = defaults.BaudRate,
            DataBits = defaults.DataBits,
            StopBits = defaults.StopBits,
            Parity = defaults.Parity,
            Handshake = defaults.Handshake,
            EncodingName = defaults.EncodingName,
            DtrEnable = false,
            RtsEnable = false,
            DiscardNull = false,
        };
        if (!await ApplySettingsSerializedAsync(session, updated))
        {
            return false;
        }

        LoadTransport(updated, includeSessionOnlyValues: true);
        return true;
    }

    internal async Task<bool> ApplyStandaloneSettingsAsync(ISerialParametersSession session, SerialPortSettings settings)
    {
        if (session.IsBusy)
        {
            return false;
        }

        return await ApplySettingsSerializedAsync(session, settings);
    }

    internal async Task<bool> FlushAsync()
    {
        _scheduler.Cancel();
        ISerialParametersSession? session = _targetSession;
        long generation = _targetGeneration;
        if (session is null)
        {
            return true;
        }

        await _transportApplyTask;
        if (!ReferenceEquals(_targetSession, session) || _targetGeneration != generation || !_transportApplyPending)
        {
            return true;
        }

        if (session.IsBusy)
        {
            _callbacks.SetStatusResource("Status.PortSettingsBusy");
            return false;
        }

        StartPendingTransportApply(session, generation);
        await _transportApplyTask;
        return !ReferenceEquals(_targetSession, session) || _targetGeneration != generation || !_transportApplyPending;
    }

    [RelayCommand]
    private async Task SelectEncodingAsync(string? encodingName)
    {
        if (_isLoading || string.IsNullOrWhiteSpace(encodingName))
        {
            return;
        }

        ISerialParametersSession? session = _targetSession ?? _callbacks.GetActiveSession();
        if (session is null || _targetSession is not null)
        {
            SerialParameterEncodingName = encodingName;
            return;
        }

        if (string.Equals(session.Settings.EncodingName, encodingName, StringComparison.OrdinalIgnoreCase))
        {
            OnPropertyChanged(nameof(ActiveEncodingName));
            return;
        }

        if (session.IsBusy)
        {
            _callbacks.SetStatusResource("Status.PortSettingsBusy");
            OnPropertyChanged(nameof(ActiveEncodingName));
            return;
        }

        if (await ApplySettingsSerializedAsync(session, session.Settings with { EncodingName = encodingName }))
        {
            Load(() => SerialParameterEncodingName = encodingName);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        End();
        _scheduler.Tick -= OnApplyTick;
        _scheduler.Dispose();
        GC.SuppressFinalize(this);
    }

    private void OnApplyTick(object? sender, EventArgs e)
    {
        ISerialParametersSession? session = _targetSession;
        if (_isLoading || !_transportApplyPending || session is null)
        {
            return;
        }

        if (session.IsBusy)
        {
            _scheduler.Schedule();
            return;
        }

        StartPendingTransportApply(session, _targetGeneration);
    }

    private void StartPendingTransportApply(ISerialParametersSession session, long generation)
    {
        Task previous = _transportApplyTask;
        _transportApplyTask = ApplyPendingTransportAfterAsync(previous, session, generation);
    }

    private async Task ApplyPendingTransportAfterAsync(Task previous, ISerialParametersSession session, long generation)
    {
        await previous;
        if (!ReferenceEquals(_targetSession, session) || _targetGeneration != generation || !_transportApplyPending) return;

        long applyingVersion = _transportChangeVersion;
        SerialPortSettings updated = session.Settings with
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
        if (ReferenceEquals(_targetSession, session) && _targetGeneration == generation)
        {
            _transportApplyPending = false;
        }
        bool succeeded = await ApplySettingsSerializedAsync(session, updated);
        if (ReferenceEquals(_targetSession, session) && _targetGeneration == generation)
        {
            _transportApplyPending = !succeeded || session.Settings != updated || _transportChangeVersion != applyingVersion;
        }
    }

    private async Task<bool> ApplySettingsSerializedAsync(ISerialParametersSession session, SerialPortSettings updated)
    {
        await _applyGate.WaitAsync();
        try
        {
            return await ApplySettingsAsync(session, updated);
        }
        finally
        {
            _applyGate.Release();
        }
    }

    private async Task<bool> ApplySettingsAsync(ISerialParametersSession session, SerialPortSettings updated)
    {
        try
        {
            await session.ApplySettingsAsync(updated);
            _callbacks.RememberPortOverride(session, updated);
            _callbacks.SetStatusResource(string.Empty);
            _callbacks.Log($"Runtime serial settings updated. Port={session.PortName}; Baud={updated.BaudRate}; DataBits={updated.DataBits}; StopBits={updated.StopBits}; Parity={updated.Parity}; Handshake={updated.Handshake}; Encoding={updated.EncodingName}; DTR={updated.DtrEnable}; RTS={updated.RtsEnable}; DiscardNull={updated.DiscardNull}", null);
            OnPropertyChanged(nameof(ActiveEncodingName));
            return true;
        }
        catch (Exception exception)
        {
            _callbacks.SetStatusResource("Status.InvalidPortSettings");
            _callbacks.Log("Runtime serial settings update failed.", exception);
            OnPropertyChanged(nameof(ActiveEncodingName));
            return false;
        }
    }

    private void LoadTransport(SerialPortSettings settings, bool includeSessionOnlyValues)
    {
        Load(() =>
        {
            SerialParameterBaudRate = settings.BaudRate;
            SerialParameterDataBits = settings.DataBits;
            SerialParameterStopBits = settings.StopBits;
            SerialParameterParity = settings.Parity;
            SerialParameterHandshake = settings.Handshake;
            SerialParameterEncodingName = settings.EncodingName;
            SerialParameterDtrEnable = includeSessionOnlyValues && settings.DtrEnable;
            SerialParameterRtsEnable = includeSessionOnlyValues && settings.RtsEnable;
            SerialParameterDiscardNull = includeSessionOnlyValues && settings.DiscardNull;
            if (!includeSessionOnlyValues)
            {
                SerialParameterAutoReconnect = false;
            }
        });
        OnPropertyChanged(nameof(ActiveEncodingName));
    }

    private void Load(Action load)
    {
        _isLoading = true;
        try
        {
            load();
        }
        finally
        {
            _isLoading = false;
        }
    }

    private void ScheduleTransportApply()
    {
        if (_isLoading || _targetSession is null)
        {
            return;
        }

        _transportApplyPending = true;
        _transportChangeVersion++;
        _scheduler.Schedule();
    }

    private void UpdateDefaultTransport(Func<SerialPortSettings, SerialPortSettings> update)
    {
        if (_isLoading)
        {
            return;
        }

        if (_targetSession is null)
        {
            _callbacks.UpdateDefaultSettings(update(_callbacks.GetDefaultSettings()));
        }
        else
        {
            ScheduleTransportApply();
        }
    }

    private void ApplySessionPreferenceChange(Action<ISerialParametersSession> update)
    {
        if (_isLoading || _targetSession is null)
        {
            return;
        }

        update(_targetSession);
        _callbacks.RememberPortOverride(_targetSession, null);
        if (_targetSession.IsOpen)
        {
            _callbacks.SetStatusResource("Status.SessionSettingRequiresReopen");
        }
    }

    private void OnTargetSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ISerialParametersSession.IsBusy))
        {
            OnPropertyChanged(nameof(IsSerialParametersEditable));
        }
    }

    partial void OnSerialParameterSendModeChanged(SendMode value)
    {
        if (_isLoading)
        {
            return;
        }

        if (_targetSession is not null)
        {
            _targetSession.SendMode = value;
            _callbacks.RememberPortOverride(_targetSession, null);
        }
        else
        {
            _callbacks.UpdateDefaultSendMode(value);
        }
    }

    partial void OnSerialParameterNewlineChanged(NewlinePolicy value)
    {
        if (_isLoading)
        {
            return;
        }

        if (_targetSession is not null)
        {
            _targetSession.Newline = value;
            _callbacks.RememberPortOverride(_targetSession, null);
        }
        else
        {
            _callbacks.UpdateDefaultNewline(value);
        }
    }

    partial void OnSerialParameterInterpretSendEscapesChanged(bool value)
    {
        if (!_isLoading && _targetSession is not null)
        {
            _targetSession.InterpretSendEscapes = value;
        }
    }

    partial void OnSerialParameterTimedSendEnabledChanged(bool value)
    {
        if (!_isLoading && _targetSession is not null)
        {
            _targetSession.TimedSendEnabled = value;
        }
    }

    partial void OnSerialParameterTimedSendIntervalMillisecondsChanged(int value)
    {
        if (!_isLoading && _targetSession is not null)
        {
            _targetSession.TimedSendIntervalMilliseconds = Math.Clamp(value, 50, 86_400_000);
        }
    }

    partial void OnSerialParameterReceiveModeChanged(ReceiveDisplayMode value) => ApplySessionPreferenceChange(session => session.ReceiveMode = value);
    partial void OnSerialParameterTimestampEnabledChanged(bool value) => ApplySessionPreferenceChange(session => session.TimestampEnabled = value);
    partial void OnSerialParameterLoggingEnabledChanged(bool value) => ApplySessionPreferenceChange(session => session.LoggingEnabled = value);
    partial void OnSerialParameterFollowEndChanged(bool value) => ApplySessionPreferenceChange(session => session.FollowEnd = value);
    partial void OnSerialParameterFilterEnabledChanged(bool value) => ApplySessionPreferenceChange(session => session.FilterEnabled = value);
    partial void OnSerialParameterBaudRateChanged(int value) => UpdateDefaultTransport(settings => settings with { BaudRate = value });
    partial void OnSerialParameterDataBitsChanged(int value) => UpdateDefaultTransport(settings => settings with { DataBits = value });
    partial void OnSerialParameterStopBitsChanged(StopBits value) => UpdateDefaultTransport(settings => settings with { StopBits = value });
    partial void OnSerialParameterParityChanged(Parity value) => UpdateDefaultTransport(settings => settings with { Parity = value });
    partial void OnSerialParameterHandshakeChanged(Handshake value) => UpdateDefaultTransport(settings => settings with { Handshake = value });
    partial void OnSerialParameterEncodingNameChanged(string value) => UpdateDefaultTransport(settings => settings with { EncodingName = value });
    partial void OnSerialParameterDtrEnableChanged(bool value) => ScheduleTransportApply();
    partial void OnSerialParameterRtsEnableChanged(bool value) => ScheduleTransportApply();
    partial void OnSerialParameterDiscardNullChanged(bool value) => ScheduleTransportApply();

    partial void OnSerialParameterAutoReconnectChanged(bool value)
    {
        if (!_isLoading && _targetSession is not null)
        {
            _targetSession.AutoReconnect = value;
            _callbacks.RememberPortOverride(_targetSession, null);
        }
    }
}
