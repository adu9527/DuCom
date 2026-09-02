using System.Collections.Concurrent;
using DuCom.Core.Logging;
using DuCom.Core.Parsing;
using DuCom.Core.Ports;
using DuCom.Core.Sending;
using DuCom.Core.Sessions;
using Xunit;

namespace DuCom.Core.Tests.Sessions;

/// <summary>
/// 2026-08-28 review round 2: the Open/Dispose runtime-publication race and the atomic
/// settings update with full rollback.
/// </summary>
public sealed class SerialSessionLifecycleHardeningTests
{
    [Fact]
    public async Task ConcurrentOpenAndDisposeNeverLeakARuntime()
    {
        using TemporaryDirectory directory = new();
        for (int iteration = 0; iteration < 30; iteration++)
        {
            FaultableTransport transport = new("RACE");
            SerialSession session = CreateSession(transport, directory.Path);

            Task<PortCommandResult> open = session.OpenAsync();
            ValueTask dispose = session.DisposeAsync();
            PortCommandResult openResult = await open;
            await dispose;

            // Whatever the interleaving, the runtime must have been drained exactly once
            // (Completed, not NotStarted) and the transport disposed exactly once.
            Assert.Equal(1, transport.DisposeCount);
            DuCom.Core.Diagnostics.PipelineMetricsSnapshot metrics = session.Snapshot().Metrics;
            Assert.Equal(
                DuCom.Core.Diagnostics.ShutdownDrainState.Completed,
                metrics.ShutdownDrainState);
            Assert.True(openResult is PortCommandResult.Succeeded or PortCommandResult.Disposed,
                $"unexpected open result {openResult}");
        }
    }

    [Fact]
    public async Task WriteTriggeredDisconnectDrainsOldRuntimeBeforeDirectReopen()
    {
        using TemporaryDirectory directory = new();
        FaultableTransport transport = new("RECONNECT") { DisconnectOnNextWrite = true };
        await using SerialSession session = CreateSession(transport, directory.Path);
        Assert.Equal(PortCommandResult.Succeeded, await session.OpenAsync());
        Assert.Equal(1, transport.DataAvailableSubscriberCount);

        await Assert.ThrowsAsync<IOException>(() =>
            session.SendAsync(SendMode.Str, "trigger-disconnect", NewlinePolicy.None).AsTask());
        Assert.Equal(PortLifecycleState.Closed, session.Status().State.State);

        Assert.Equal(PortCommandResult.Succeeded, await session.OpenAsync());

        Assert.Equal(2, transport.OpenCount);
        Assert.Equal(1, transport.DataAvailableSubscriberCount);
        Assert.Equal(PortCommandResult.Succeeded, await session.CloseAsync());
    }

    [Fact]
    public async Task ApplySettingsCancellationLeavesSessionUnchanged()
    {
        using TemporaryDirectory directory = new();
        FaultableTransport transport = new("SET");
        SerialSession session = CreateSession(transport, directory.Path);
        await session.OpenAsync();
        SerialPortSettings original = session.Settings;
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            session.ApplySettingsAsync(original with { BaudRate = 576_000 }, cancelled.Token));

        Assert.Equal(original, session.Settings);
        Assert.Equal(original, transport.Settings);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task CancelledFormatterSwapRollsTransportBackToPreviousSettings()
    {
        using TemporaryDirectory directory = new();
        FaultableTransport transport = new("SET");
        SerialSession session = CreateSession(transport, directory.Path);
        await session.OpenAsync();
        SerialPortSettings original = session.Settings;

        // Cancel the update the moment the transport has applied it: publishing the new
        // capture profile must abort and roll the transport back.
        using CancellationTokenSource cancellation = new();
        transport.OnApplied = cancellation.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            session.ApplySettingsAsync(original with { EncodingName = "ascii" }, cancellation.Token));

        Assert.Equal(original, session.Settings);
        Assert.Equal(original, transport.Settings);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task RollbackFailureSurfacesBothFailuresInOneAggregate()
    {
        using TemporaryDirectory directory = new();
        FaultableTransport transport = new("SET");
        // Fail the SECOND ApplySettings call (the rollback of the cancelled update).
        transport.FailApplySettingsFromCall = 2;
        SerialSession session = CreateSession(transport, directory.Path);
        await session.OpenAsync();
        SerialPortSettings original = session.Settings;

        using CancellationTokenSource cancellation = new();
        transport.OnApplied = cancellation.Cancel;

        AggregateException failure = await Assert.ThrowsAsync<AggregateException>(() =>
            session.ApplySettingsAsync(original with { EncodingName = "ascii" }, cancellation.Token));

        Assert.Equal(2, failure.InnerExceptions.Count);
        Assert.Contains(failure.InnerExceptions, exception => exception is OperationCanceledException);
        Assert.Contains(failure.InnerExceptions, exception => exception.Message.Contains("simulated apply failure", StringComparison.Ordinal));
        Assert.Contains("mixed configuration", failure.Message, StringComparison.Ordinal);
        await session.DisposeAsync();
    }

    private static SerialSession CreateSession(ISerialTransport transport, string logDirectory)
    {
        return new SerialSession(
            transport,
            SerialPortSettings.Default(transport.Settings.PortName),
            ReceiveDisplayMode.Str,
            timestampEnabled: false,
            new SessionLogWriterOptions(logDirectory, transport.Settings.PortName),
            lineBudgetBytes: 1024 * 1024);
    }

    private sealed class FaultableTransport(string portName) : ISerialTransport, ISerialSettingsTransport
    {
        private readonly ConcurrentQueue<byte[]> _received = new();
        private EventHandler? _dataAvailable;
        private EventHandler<TransportDisconnectedEventArgs>? _disconnected;

        public int DisposeCount { get; private set; }

        public int ApplySettingsCalls { get; private set; }

        public int OpenCount { get; private set; }

        public int CloseCount { get; private set; }

        public int DataAvailableSubscriberCount => _dataAvailable?.GetInvocationList().Length ?? 0;

        public bool DisconnectOnNextWrite { get; set; }

        /// <summary>When set, the Nth ApplySettings call throws (simulating transport/rollback failure).</summary>
        public int FailApplySettingsFromCall { get; set; }

        /// <summary>Test hook invoked after a successful apply — used to cancel mid-update.</summary>
        public Action? OnApplied { get; set; }

        public event EventHandler? DataAvailable
        {
            add => _dataAvailable += value;
            remove => _dataAvailable -= value;
        }

        public event EventHandler<TransportDisconnectedEventArgs>? Disconnected
        {
            add => _disconnected += value;
            remove => _disconnected -= value;
        }

        public SerialPortSettings Settings { get; private set; } = SerialPortSettings.Default(portName);

        public int BytesAvailable => _received.TryPeek(out byte[]? payload) ? payload.Length : 0;

        public ValueTask OpenAsync(CancellationToken cancellationToken)
        {
            OpenCount++;
            return ValueTask.CompletedTask;
        }

        public ValueTask CloseAsync(CancellationToken cancellationToken)
        {
            CloseCount++;
            return ValueTask.CompletedTask;
        }

        public int Read(Span<byte> destination)
        {
            if (!_received.TryDequeue(out byte[]? payload))
            {
                return 0;
            }

            payload.CopyTo(destination);
            return payload.Length;
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            if (DisconnectOnNextWrite)
            {
                DisconnectOnNextWrite = false;
                IOException exception = new("simulated write disconnect");
                _disconnected?.Invoke(this, new TransportDisconnectedEventArgs(exception));
                throw exception;
            }

            return ValueTask.CompletedTask;
        }

        public void ApplySettings(SerialPortSettings settings)
        {
            ApplySettingsCalls++;
            if (FailApplySettingsFromCall > 0 && ApplySettingsCalls >= FailApplySettingsFromCall)
            {
                throw new IOException($"simulated apply failure on call {ApplySettingsCalls}");
            }

            Settings = settings;
            OnApplied?.Invoke();
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory() => Path = Directory.CreateTempSubdirectory("ducom-session-hardening-").FullName;

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
