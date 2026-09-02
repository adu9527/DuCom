using System.Collections.Concurrent;
using DuCom.Core.Diagnostics;
using DuCom.Core.Logging;
using DuCom.Core.Parsing;
using DuCom.Core.Ports;
using DuCom.Core.Sessions;
using Xunit;

namespace DuCom.Core.Tests.Sessions;

/// <summary>
/// ADR-0004 follow-up: sustained-input close budget at the session level and the shared
/// Close/Dispose ordering (receive quiesce/drain, transport close, then log-side drain).
/// </summary>
public sealed class SerialSessionCloseBudgetTests
{
    [Fact]
    public async Task CloseUnderContinuousInputReportsFaultedWithBudgetReason()
    {
        using TemporaryDirectory directory = new();
        ContinuousSerialTransport transport = new();
        SerialSession session = CreateSession(transport, directory.Path);
        await session.OpenAsync();

        PortCommandResult result = await session.CloseAsync();

        Assert.Equal(PortCommandResult.Faulted, result);
        Assert.NotNull(session.Status().Fault);
        // The fault may come from any bounded phase (capacity wait, blocked read, or the
        // per-iteration budget check); every phase names the exceeded budget and the
        // explicit session-fault outcome. The message is included in the failure output.
        string faultMessage = session.Status().Fault!.Message;
        Assert.True(
            faultMessage.Contains("was exceeded", StringComparison.Ordinal) ||
            faultMessage.Contains("drain exceeded", StringComparison.Ordinal),
            $"unexpected budget fault message: {faultMessage}");
        Assert.True(faultMessage.Contains("silent loss", StringComparison.Ordinal), $"fault must state the explicit outcome: {faultMessage}");
        Assert.Equal(ShutdownDrainState.Faulted, session.Status().Metrics.ShutdownDrainState);
        Assert.True(session.Status().Metrics.Faults >= 1);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task DisposeUnderContinuousInputClosesTransportAndDoesNotHang()
    {
        using TemporaryDirectory directory = new();
        ContinuousSerialTransport transport = new();
        SerialSession session = CreateSession(transport, directory.Path);
        await session.OpenAsync();

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        await session.DisposeAsync().AsTask().WaitAsync(timeout.Token);

        Assert.Equal(1, transport.CloseCount);
        Assert.Equal(1, transport.DisposeCount);
        Assert.True(transport.ReadsWhileOpen > 0);
        Assert.NotNull(session.Status().Fault);
    }

    [Fact]
    public async Task DisposeWithDriverBufferedBacklogLogsEveryByteAndClosesOnce()
    {
        using TemporaryDirectory directory = new();
        DriverLikeTransport transport = new();
        SerialSession session = CreateSession(transport, directory.Path);
        await session.OpenAsync();
        transport.LoadBuffer("driver-bytes\r\n"u8.ToArray());

        await session.DisposeAsync();

        Assert.Equal(1, transport.CloseCount);
        Assert.Equal(1, transport.DisposeCount);
        Assert.Equal(1, transport.ReadsWhileOpen);
        Assert.Equal("driver-bytes\r\n", ReadLogs(directory.Path));
    }

    [Fact]
    public async Task ApplySettingsAsyncRejectsTransportsWithoutTheComCapability()
    {
        using TemporaryDirectory directory = new();
        NonConfigurableTransport transport = new();
        SerialSession session = CreateSession(transport, directory.Path);
        await session.OpenAsync();

        SerialPortSettings updated = transport.Settings with { BaudRate = 576_00 };
        await Assert.ThrowsAsync<NotSupportedException>(() => session.ApplySettingsAsync(updated));

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

    private static string ReadLogs(string directory) => string.Concat(
        Directory.Exists(directory)
            ? Directory.GetFiles(directory, "*.txt").Order().Select(File.ReadAllText)
            : []);

    /// <summary>Keeps producing bytes forever; a close can never finish draining it.</summary>
    private sealed class ContinuousSerialTransport : ISerialTransport
    {
        private const int ChunkSize = 64;
        private int _closeCount;
        private int _disposeCount;
        private int _readsWhileOpen;

        public event EventHandler? DataAvailable
        {
            add { }
            remove { }
        }

        public event EventHandler<TransportDisconnectedEventArgs>? Disconnected
        {
            add { }
            remove { }
        }

        public SerialPortSettings Settings { get; private set; } = SerialPortSettings.Default("CONT");

        public int CloseCount => Volatile.Read(ref _closeCount);

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public int ReadsWhileOpen => Volatile.Read(ref _readsWhileOpen);

        public int BytesAvailable => ChunkSize;

        public ValueTask OpenAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask CloseAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _closeCount);
            return ValueTask.CompletedTask;
        }

        public int Read(Span<byte> destination)
        {
            Interlocked.Increment(ref _readsWhileOpen);
            for (int index = 0; index < ChunkSize; index++)
            {
                destination[index] = (byte)index;
            }

            return ChunkSize;
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Driver-like fake: close discards the queue and closes reads afterwards.</summary>
    private sealed class DriverLikeTransport : ISerialTransport
    {
        private readonly ConcurrentQueue<byte[]> _driverBuffer = new();
        private int _isOpen = 1;
        private int _closeCount;
        private int _disposeCount;
        private int _readsWhileOpen;

        public event EventHandler? DataAvailable
        {
            add { }
            remove { }
        }

        public event EventHandler<TransportDisconnectedEventArgs>? Disconnected
        {
            add { }
            remove { }
        }

        public SerialPortSettings Settings { get; private set; } = SerialPortSettings.Default("DRIVER");

        public int CloseCount => Volatile.Read(ref _closeCount);

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public int ReadsWhileOpen => Volatile.Read(ref _readsWhileOpen);

        public int BytesAvailable =>
            Volatile.Read(ref _isOpen) == 1 && _driverBuffer.TryPeek(out byte[]? payload) ? payload.Length : 0;

        public ValueTask OpenAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask CloseAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _closeCount);
            Volatile.Write(ref _isOpen, 0);
            _driverBuffer.Clear();
            return ValueTask.CompletedTask;
        }

        public int Read(Span<byte> destination)
        {
            if (Volatile.Read(ref _isOpen) != 1)
            {
                throw new InvalidOperationException("The port is closed.");
            }

            if (!_driverBuffer.TryDequeue(out byte[]? payload))
            {
                return 0;
            }

            payload.CopyTo(destination);
            Interlocked.Increment(ref _readsWhileOpen);
            return payload.Length;
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            return ValueTask.CompletedTask;
        }

        public void LoadBuffer(byte[] payload) => _driverBuffer.Enqueue(payload);
    }

    /// <summary>Transport-neutral fake that deliberately lacks ISerialSettingsTransport.</summary>
    private sealed class NonConfigurableTransport : ISerialTransport
    {
        private readonly ConcurrentQueue<byte[]> _received = new();

        public event EventHandler? DataAvailable
        {
            add { }
            remove { }
        }

        public event EventHandler<TransportDisconnectedEventArgs>? Disconnected
        {
            add { }
            remove { }
        }

        public SerialPortSettings Settings { get; private set; } = SerialPortSettings.Default("PLAIN");

        public int BytesAvailable => _received.TryPeek(out byte[]? payload) ? payload.Length : 0;

        public ValueTask OpenAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask CloseAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public int Read(Span<byte> destination)
        {
            if (!_received.TryDequeue(out byte[]? payload))
            {
                return 0;
            }

            payload.CopyTo(destination);
            return payload.Length;
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory() => Path = Directory.CreateTempSubdirectory("ducom-close-budget-").FullName;

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
