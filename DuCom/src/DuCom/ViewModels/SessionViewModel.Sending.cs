using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using DuCom.Core.Ports;
using DuCom.Core.Sending;

namespace DuCom.ViewModels;

public partial class SessionViewModel
{
    private readonly DispatcherTimer _timedSendTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _timedSendInProgress;

    [ObservableProperty]
    public partial string SendText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool InterpretSendEscapes { get; set; }

    [ObservableProperty]
    public partial bool TimedSendEnabled { get; set; }

    [ObservableProperty]
    public partial int TimedSendIntervalMilliseconds { get; set; } = 1000;

    partial void OnTimedSendEnabledChanged(bool value) => UpdateTimedSendTimer();

    partial void OnTimedSendIntervalMillisecondsChanged(int value) => UpdateTimedSendTimer();

    [ObservableProperty]
    public partial SendMode SendMode { get; set; } = SendMode.Str;

    [ObservableProperty]
    public partial NewlinePolicy Newline { get; set; } = NewlinePolicy.None;

    public async Task SendAsync(CancellationToken cancellationToken = default)
    {
        if (!IsOpen || string.IsNullOrEmpty(SendText))
        {
            return;
        }

        string payload = SendMode == SendMode.Str && InterpretSendEscapes
            ? SendEscapeDecoder.Decode(SendText)
            : SendText;
        await _session.SendAsync(SendMode, payload, Newline, cancellationToken);
    }

    private void UpdateTimedSendTimer()
    {
        _timedSendTimer.Stop();
        if (TimedSendEnabled)
        {
            _timedSendTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(TimedSendIntervalMilliseconds, 50, 86_400_000));
            _timedSendTimer.Start();
        }
    }

    private async void OnTimedSendTick(object? sender, EventArgs e)
    {
        if (_timedSendInProgress || !IsOpen || string.IsNullOrEmpty(SendText))
        {
            return;
        }

        _timedSendInProgress = true;
        try
        {
            await SendAsync();
        }
        catch (Exception exception)
        {
            Program.DiagnosticLog?.Warning($"Timed send failed. Port={PortName}.", exception);
        }
        finally
        {
            _timedSendInProgress = false;
        }
    }

    /// <summary>
    /// Sends a raw command through the session (STR, no appended newline) — used by
    /// watchdog and Telnet bridge actions. Safe to call from background threads: the open
    /// check reads the thread-safe Core status snapshot and the payload mode is fixed
    /// instead of reading mutable ViewModel send options.
    /// </summary>
    public async Task SendRawCommandAsync(string command, CancellationToken cancellationToken = default)
    {
        if (_session.Status.State.State != PortLifecycleState.Open)
        {
            throw new InvalidOperationException("The session is not open.");
        }

        await _session.SendAsync(SendMode.Str, command, NewlinePolicy.None, cancellationToken);
    }
}
