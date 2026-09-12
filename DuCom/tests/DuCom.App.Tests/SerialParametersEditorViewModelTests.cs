using System.ComponentModel;
using System.IO.Ports;
using DuCom.Core.Parsing;
using DuCom.Core.Ports;
using DuCom.Core.Sending;
using DuCom.ViewModels;
using Xunit;

namespace DuCom.App.Tests;

public sealed class SerialParametersEditorViewModelTests
{
    [Fact]
    public void BeginDefaultLoadsDefaultsAndUpdatesThemImmediately()
    {
        using Harness harness = new();

        harness.Editor.BeginDefault(harness.Defaults, SendMode.Hex, NewlinePolicy.CrLf);

        Assert.False(harness.Editor.IsEditingPortSettings);
        Assert.Equal(harness.Defaults.BaudRate, harness.Editor.SerialParameterBaudRate);
        Assert.Equal(SendMode.Hex, harness.Editor.SerialParameterSendMode);
        harness.Editor.SerialParameterDataBits = 7;
        harness.Editor.SerialParameterEncodingName = "gbk";

        Assert.Equal(7, harness.Defaults.DataBits);
        Assert.Equal("gbk", harness.Defaults.EncodingName);
        Assert.Equal(0, harness.Scheduler.ScheduleCount);
    }

    [Fact]
    public void BeginSessionLoadsSessionAndTransportChangeSignalsDebounce()
    {
        using Harness harness = new();
        FakeSession session = new(Settings(115_200)) { SendMode = SendMode.Hex, ReceiveMode = ReceiveDisplayMode.Hex };

        harness.Editor.BeginSession(session);
        harness.Editor.SerialParameterBaudRate = 230_400;

        Assert.True(harness.Editor.IsEditingPortSettings);
        Assert.Equal(SendMode.Hex, harness.Editor.SerialParameterSendMode);
        Assert.Equal(ReceiveDisplayMode.Hex, harness.Editor.SerialParameterReceiveMode);
        Assert.True(harness.Editor.HasPendingTransportChanges);
        Assert.Equal(1, harness.Scheduler.ScheduleCount);
        Assert.Equal(115_200, session.Settings.BaudRate);
    }

    [Fact]
    public async Task BusyFlushVetoesCloseAndKeepsPending()
    {
        using Harness harness = new();
        FakeSession session = new(Settings(115_200)) { IsBusy = true };
        harness.Editor.BeginSession(session);
        harness.Editor.SerialParameterBaudRate = 230_400;

        bool flushed = await harness.Editor.FlushAsync();

        Assert.False(flushed);
        Assert.True(harness.Editor.HasPendingTransportChanges);
        Assert.Equal("Status.PortSettingsBusy", harness.StatusResource);
        Assert.Equal(0, session.ApplyCount);
    }

    [Fact]
    public void BusyDebounceTickReschedulesWithoutApplying()
    {
        using Harness harness = new();
        FakeSession session = new(Settings(115_200)) { IsBusy = true };
        harness.Editor.BeginSession(session);
        harness.Editor.SerialParameterBaudRate = 230_400;

        harness.Scheduler.Fire();

        Assert.Equal(2, harness.Scheduler.ScheduleCount);
        Assert.Equal(0, session.ApplyCount);
        Assert.True(harness.Editor.HasPendingTransportChanges);
    }

    [Fact]
    public async Task FlushAppliesPendingSettingsAndClearsPendingOnSuccess()
    {
        using Harness harness = new();
        FakeSession session = new(Settings(115_200));
        harness.Editor.BeginSession(session);
        harness.Editor.SerialParameterBaudRate = 230_400;

        bool flushed = await harness.Editor.FlushAsync();

        Assert.True(flushed);
        Assert.False(harness.Editor.HasPendingTransportChanges);
        Assert.Equal(230_400, session.Settings.BaudRate);
        Assert.Equal(1, session.ApplyCount);
        Assert.Equal(1, harness.RememberCount);
    }

    [Fact]
    public async Task FlushWaitsForApplyAlreadyStartedByScheduler()
    {
        using Harness harness = new();
        FakeSession session = new(Settings(115_200)) { ApplyBlocker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        harness.Editor.BeginSession(session);
        harness.Editor.SerialParameterBaudRate = 230_400;

        harness.Scheduler.Fire();
        await session.ApplyStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<bool> flush = harness.Editor.FlushAsync();

        Assert.False(flush.IsCompleted);
        session.ApplyBlocker.SetResult();
        Assert.True(await flush);
        Assert.Equal(230_400, session.Settings.BaudRate);
    }

    [Fact]
    public async Task OldApplyCompletionDoesNotClearNewTargetPendingChange()
    {
        using Harness harness = new();
        FakeSession first = new(Settings(115_200)) { ApplyBlocker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        FakeSession second = new(Settings(9_600));
        harness.Editor.BeginSession(first);
        harness.Editor.SerialParameterBaudRate = 230_400;
        harness.Scheduler.Fire();
        await first.ApplyStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        harness.Editor.BeginSession(second);
        harness.Editor.SerialParameterBaudRate = 38_400;
        first.ApplyBlocker.SetResult();
        await first.ApplyCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Same(second, harness.Editor.TargetSession);
        Assert.True(harness.Editor.HasPendingTransportChanges);
        Assert.True(await harness.Editor.FlushAsync());
        Assert.Equal(38_400, second.Settings.BaudRate);
    }

    [Fact]
    public async Task FailedApplyRestoresPendingAndKeepsActiveEncodingAppliedValue()
    {
        using Harness harness = new();
        FakeSession session = new(Settings(115_200)) { ApplyException = new InvalidOperationException("invalid") };
        harness.Editor.BeginSession(session);
        harness.Editor.SerialParameterEncodingName = "gbk";

        bool flushed = await harness.Editor.FlushAsync();

        Assert.False(flushed);
        Assert.True(harness.Editor.HasPendingTransportChanges);
        Assert.Equal("utf-8", session.Settings.EncodingName);
        Assert.Equal("utf-8", harness.Editor.ActiveEncodingName);
        Assert.Equal("Status.InvalidPortSettings", harness.StatusResource);
    }

    [Fact]
    public void SessionPreferencesApplyImmediatelyWithoutTransportDebounce()
    {
        using Harness harness = new();
        FakeSession session = new(Settings(115_200)) { IsOpen = true };
        harness.Editor.BeginSession(session);

        harness.Editor.SerialParameterReceiveMode = ReceiveDisplayMode.Hex;
        harness.Editor.SerialParameterTimestampEnabled = false;
        harness.Editor.SerialParameterAutoReconnect = true;

        Assert.Equal(ReceiveDisplayMode.Hex, session.ReceiveMode);
        Assert.False(session.TimestampEnabled);
        Assert.True(session.AutoReconnect);
        Assert.Equal(0, harness.Scheduler.ScheduleCount);
        Assert.Equal(3, harness.RememberCount);
        Assert.Equal("Status.SessionSettingRequiresReopen", harness.StatusResource);
    }

    [Fact]
    public async Task EncodingCommandUsesEditorWriterForTargetAndDirectApplyForActiveSession()
    {
        using Harness harness = new();
        FakeSession edited = new(Settings(115_200));
        harness.Editor.BeginSession(edited);

        await harness.Editor.SelectEncodingCommand.ExecuteAsync("gbk");

        Assert.True(harness.Editor.HasPendingTransportChanges);
        Assert.Equal(0, edited.ApplyCount);
        Assert.Equal("utf-8", harness.Editor.ActiveEncodingName);

        harness.Editor.End();
        FakeSession active = new(Settings(9_600));
        harness.ActiveSession = active;
        await harness.Editor.SelectEncodingCommand.ExecuteAsync("gbk");

        Assert.Equal(1, active.ApplyCount);
        Assert.Equal("gbk", active.Settings.EncodingName);
        Assert.Equal("gbk", harness.Editor.ActiveEncodingName);
    }

    private static SerialPortSettings Settings(int baudRate) => SerialPortSettings.Default("COM1") with
    {
        BaudRate = baudRate,
        EncodingName = "utf-8",
    };

    private sealed class Harness : IDisposable
    {
        public Harness()
        {
            Scheduler = new FakeScheduler();
            Defaults = Settings(1_152_000) with { PortName = string.Empty };
            Editor = new SerialParametersEditorViewModel(
                new SerialParametersEditorCallbacks(
                    () => Defaults,
                    value => Defaults = value,
                    value => DefaultSendMode = value,
                    value => DefaultNewline = value,
                    () => ActiveSession,
                    (_, _) => RememberCount++,
                    value => StatusResource = value,
                    (_, _) => { }),
                Scheduler);
        }

        public SerialParametersEditorViewModel Editor { get; }
        public FakeScheduler Scheduler { get; }
        public SerialPortSettings Defaults { get; set; }
        public ISerialParametersSession? ActiveSession { get; set; }
        public SendMode DefaultSendMode { get; set; }
        public NewlinePolicy DefaultNewline { get; set; }
        public int RememberCount { get; set; }
        public string StatusResource { get; set; } = string.Empty;

        public void Dispose() => Editor.Dispose();
    }

    private sealed class FakeScheduler : ISerialParametersApplyScheduler
    {
        public event EventHandler? Tick;
        public int ScheduleCount { get; private set; }
        public int CancelCount { get; private set; }
        public void Schedule() => ScheduleCount++;
        public void Cancel() => CancelCount++;
        public void Dispose() { }
        public void Fire() => Tick?.Invoke(this, EventArgs.Empty);
    }

    private sealed class FakeSession : ISerialParametersSession
    {
        public FakeSession(SerialPortSettings settings) => Settings = settings;

        public event PropertyChangedEventHandler? PropertyChanged
        {
            add { }
            remove { }
        }
        public string PortName => Settings.PortName;
        public SerialPortSettings Settings { get; private set; }
        public bool IsBusy { get; set; }
        public bool IsOpen { get; set; }
        public SendMode SendMode { get; set; }
        public NewlinePolicy Newline { get; set; }
        public bool InterpretSendEscapes { get; set; }
        public bool TimedSendEnabled { get; set; }
        public int TimedSendIntervalMilliseconds { get; set; } = 1000;
        public bool AutoReconnect { get; set; }
        public ReceiveDisplayMode ReceiveMode { get; set; } = ReceiveDisplayMode.Str;
        public bool TimestampEnabled { get; set; } = true;
        public bool LoggingEnabled { get; set; } = true;
        public bool FollowEnd { get; set; } = true;
        public bool FilterEnabled { get; set; } = true;
        public Exception? ApplyException { get; set; }
        public int ApplyCount { get; private set; }
        public TaskCompletionSource? ApplyBlocker { get; set; }
        public TaskCompletionSource ApplyStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ApplyCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task ApplySettingsAsync(SerialPortSettings settings, CancellationToken cancellationToken = default)
        {
            ApplyCount++;
            ApplyStarted.TrySetResult();
            if (ApplyBlocker is not null) await ApplyBlocker.Task.WaitAsync(cancellationToken);
            if (ApplyException is not null)
            {
                throw ApplyException;
            }

            Settings = settings;
            ApplyCompleted.TrySetResult();
        }
    }
}
