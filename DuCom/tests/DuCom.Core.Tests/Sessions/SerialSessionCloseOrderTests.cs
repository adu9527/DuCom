using System.Text;
using DuCom.Core.Logging;
using DuCom.Core.Parsing;
using DuCom.Core.Ports;
using DuCom.Core.Sending;
using DuCom.Core.Sessions;
using DuCom.Core.Storage;
using DuCom.Core.Diagnostics;

namespace DuCom.Core.Tests.Sessions;

/// <summary>
/// Close-order tests using a driver-like transport. Unlike the in-memory fakes, this
/// transport mirrors real SerialPort behavior: BytesToRead reports 0 once the port is
/// closed, Close() discards the driver receive queue, and Read() after close throws.
/// These tests prove the session closes with quiesce/drain BEFORE the port close
/// (ADR-0004); against the old close-first order they fail with silent data loss.
/// </summary>
public sealed class SerialSessionCloseOrderTests
{
    [Fact]
    public async Task CloseDrainsDriverBufferThatNeverFiredDataAvailable()
    {
        using TemporaryDirectory directory = new();
        DriverLikeSerialTransport transport = new("COM-DR1");
        await using SerialSession session = CreateSession(transport, directory.Path);
        await session.OpenAsync();

        // Bytes sit in the driver buffer; no DataAvailable event was ever raised.
        transport.LoadBuffer("late-line\r\n"u8.ToArray());
        PortCommandResult result = await session.CloseAsync();

        Assert.Equal(PortCommandResult.Succeeded, result);
        Assert.True(transport.ReadsWhileOpen >= 1, "drain must read while the port is still open");
        Assert.Equal("late-line\r\n\r\n", ReadLogs(directory.Path));
        Assert.Equal("late-line", Assert.Single(session.Snapshot().Lines.Lines).Text);
        SerialSessionSnapshot snapshot = session.Snapshot();
        Assert.Null(snapshot.Fault);
        Assert.True(snapshot.Metrics.IsLogFormattingCoverageComplete);
    }

    [Fact]
    public async Task CloseDrainsBacklogLargerThanChannelCapacity()
    {
        using TemporaryDirectory directory = new();
        DriverLikeSerialTransport transport = new("COM-DR2");
        await using SerialSession session = CreateSession(transport, directory.Path);
        await session.OpenAsync();

        const int blockCount = 300; // exceeds the 256-block receive Channel capacity
        byte[] payload = "data\n"u8.ToArray();
        for (int index = 0; index < blockCount; index++)
        {
            transport.LoadBuffer(payload);
        }

        PortCommandResult result = await session.CloseAsync();

        Assert.Equal(PortCommandResult.Succeeded, result);
        SerialSessionSnapshot snapshot = session.Snapshot();
        Assert.Equal(blockCount, snapshot.Metrics.ProducedBlocks);
        Assert.Equal(snapshot.Metrics.ProducedBlocks, snapshot.Metrics.AcceptedBlocks);
        Assert.Equal(snapshot.Metrics.AcceptedBlocks, snapshot.Metrics.FormattedLogBlocks);
        Assert.Equal(0, snapshot.Metrics.Faults);
        Assert.Equal(ShutdownDrainState.Completed, snapshot.Metrics.ShutdownDrainState);
        string log = ReadLogs(directory.Path);
        // The session log normalizes LF to CRLF, so each "data\n" block formats to 6 bytes.
        Assert.Equal(blockCount * (payload.Length + 1) + 2, Encoding.UTF8.GetByteCount(log));
    }

    [Fact]
    public async Task CallbackRacingCloseAccountsForEveryLoadedByte()
    {
        using TemporaryDirectory directory = new();
        DriverLikeSerialTransport transport = new("COM-DR3");
        await using SerialSession session = CreateSession(transport, directory.Path);
        await session.OpenAsync();

        const int blockCount = 500;
        for (int index = 0; index < blockCount; index++)
        {
            transport.LoadBuffer("x\n"u8.ToArray());
        }

        using ManualResetEventSlim stopFiring = new();
        Task firingTask = Task.Run(() =>
        {
            while (!stopFiring.IsSet)
            {
                transport.RaiseDataAvailable();
            }
        });

        PortCommandResult result;
        try
        {
            result = await session.CloseAsync();
        }
        finally
        {
            stopFiring.Set();
            await AwaitFiringSafeAsync(firingTask);
        }

        Assert.Equal(PortCommandResult.Succeeded, result);
        SerialSessionSnapshot snapshot = session.Snapshot();
        Assert.Equal(blockCount, snapshot.Metrics.ProducedBlocks);
        Assert.Equal(snapshot.Metrics.ProducedBlocks, snapshot.Metrics.AcceptedBlocks);
        Assert.Equal(0, snapshot.Metrics.Faults);
        Assert.Null(snapshot.Fault);
        string log = ReadLogs(directory.Path);
        // Each "x\n" block formats to "x\r\n" (3 bytes) by newline normalization.
        Assert.Equal(blockCount * 3 + 2, Encoding.UTF8.GetByteCount(log));
    }

    [Fact]
    public async Task TransportCloseFaultStillPreservesDrainedDataAndReportsFault()
    {
        using TemporaryDirectory directory = new();
        DriverLikeSerialTransport transport = new("COM-DR4")
        {
            CloseException = new IOException("driver close failed"),
        };
        await using SerialSession session = CreateSession(transport, directory.Path);
        await session.OpenAsync();
        transport.LoadBuffer("before-fault\r\n"u8.ToArray());

        PortCommandResult result = await session.CloseAsync();

        Assert.Equal(PortCommandResult.Faulted, result);
        Assert.NotNull(session.Snapshot().Fault);
        // The drain already ran before the failed close attempt: data is preserved.
        Assert.Contains("before-fault", ReadLogs(directory.Path), StringComparison.Ordinal);
        Assert.True(transport.ReadsWhileOpen >= 1);
    }

    [Fact]
    public async Task DrainReadFaultIsExplicitSessionFaultNotSilentSuccess()
    {
        using TemporaryDirectory directory = new();
        DriverLikeSerialTransport transport = new("COM-DR5");
        await using SerialSession session = CreateSession(transport, directory.Path);
        await session.OpenAsync();
        transport.LoadBuffer("doomed\r\n"u8.ToArray());
        transport.FailReads = true;

        PortCommandResult result = await session.CloseAsync();

        Assert.Equal(PortCommandResult.Faulted, result);
        SerialSessionSnapshot snapshot = session.Snapshot();
        Assert.NotNull(snapshot.Fault);
        Assert.Equal("ReceivePipeline", snapshot.Fault.Source);
        Assert.True(snapshot.Metrics.Faults > 0);
        // The port is still closed even after a drain fault.
        Assert.Equal(PortLifecycleState.Closed, snapshot.State.State);
        Assert.Equal(1, transport.CloseCount);
    }

    [Fact]
    public async Task DisposeDrainsDriverBufferBeforeClosingTransport()
    {
        using TemporaryDirectory directory = new();
        DriverLikeSerialTransport transport = new("COM-DR6");
        SerialSession session = CreateSession(transport, directory.Path);
        await session.OpenAsync();
        transport.LoadBuffer("dispose-drain\r\n"u8.ToArray());

        await session.DisposeAsync();
        await session.DisposeAsync();

        Assert.Equal(1, transport.CloseCount);
        Assert.Equal(1, transport.DisposeCount);
        Assert.True(transport.ReadsWhileOpen >= 1);
        Assert.Equal("dispose-drain\r\n", ReadLogs(directory.Path));
    }

    [Fact]
    public async Task TwoDriverLikeSessionsDrainIsolated()
    {
        using TemporaryDirectory firstDirectory = new();
        using TemporaryDirectory secondDirectory = new();
        DriverLikeSerialTransport firstTransport = new("COM-DR7");
        DriverLikeSerialTransport secondTransport = new("COM-DR8");
        await using SerialSession first = CreateSession(firstTransport, firstDirectory.Path);
        await using SerialSession second = CreateSession(secondTransport, secondDirectory.Path);
        await first.OpenAsync();
        await second.OpenAsync();

        firstTransport.LoadBuffer("one\r\n"u8.ToArray());
        secondTransport.LoadBuffer("two\r\n"u8.ToArray());

        Assert.Equal(PortCommandResult.Succeeded, await first.CloseAsync());
        Assert.Equal(PortCommandResult.Succeeded, await second.CloseAsync());

        Assert.Equal("one\r\n\r\n", ReadLogs(firstDirectory.Path));
        Assert.Equal("two\r\n\r\n", ReadLogs(secondDirectory.Path));
        Assert.Null(first.Snapshot().Fault);
        Assert.Null(second.Snapshot().Fault);
    }

    private static async Task AwaitFiringSafeAsync(Task task)
    {
        try
        {
            await task;
        }
        catch
        {
            // The firing loop must not fail the test by racing shutdown.
        }
    }

    private static SerialSession CreateSession(ISerialTransport transport, string logDirectory)
    {
        SerialPortSettings settings = SerialPortSettings.Default(transport.Settings.PortName);
        return new SerialSession(
            transport,
            settings,
            ReceiveDisplayMode.Str,
            timestampEnabled: false,
            new SessionLogWriterOptions(logDirectory, settings.PortName),
            lineBudgetBytes: 1024 * 1024);
    }

    private static string ReadLogs(string directory) => string.Concat(
        Directory.Exists(directory)
            ? Directory.GetFiles(directory, "*.txt").Order().Select(File.ReadAllText)
            : []);

    /// <summary>
    /// Mirrors real SerialPort driver behavior: after Close() the receive queue is discarded,
    /// BytesToRead reports 0, and Read() on a closed port throws.
    /// </summary>
    private sealed class DriverLikeSerialTransport(string portName) : ISerialTransport
    {
        private readonly object _gate = new();
        private readonly Queue<byte[]> _driverBuffer = new();
        private int _isOpen;

        public event EventHandler? DataAvailable;

        public event EventHandler<TransportDisconnectedEventArgs>? Disconnected
        {
            add { }
            remove { }
        }

        public SerialPortSettings Settings { get; private set; } = SerialPortSettings.Default(portName);

        public Exception? CloseException { get; init; }

        public bool FailReads { get; set; }

        public int CloseCount { get; private set; }

        public int DisposeCount { get; private set; }

        public int ReadsWhileOpen { get; private set; }

        public int BytesAvailable
        {
            get
            {
                lock (_gate)
                {
                    return Volatile.Read(ref _isOpen) == 1 && _driverBuffer.TryPeek(out byte[]? payload)
                        ? payload.Length
                        : 0;
                }
            }
        }

        public ValueTask OpenAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Volatile.Write(ref _isOpen, 1);
            return ValueTask.CompletedTask;
        }

        public ValueTask CloseAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CloseCount++;
            if (CloseException is not null)
            {
                throw CloseException;
            }

            lock (_gate)
            {
                Volatile.Write(ref _isOpen, 0);
                _driverBuffer.Clear(); // the driver discards unread bytes on close
            }

            return ValueTask.CompletedTask;
        }

        public int Read(Span<byte> destination)
        {
            lock (_gate)
            {
                if (Volatile.Read(ref _isOpen) != 1)
                {
                    throw new InvalidOperationException("The port is closed.");
                }

                if (FailReads)
                {
                    throw new IOException("driver read failed");
                }

                byte[] payload = _driverBuffer.Dequeue();
                payload.CopyTo(destination);
                ReadsWhileOpen++;
                return payload.Length;
            }
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }

        /// <summary>Queues bytes in the driver buffer without raising DataAvailable.</summary>
        public void LoadBuffer(byte[] payload)
        {
            lock (_gate)
            {
                _driverBuffer.Enqueue(payload);
            }
        }

        public void RaiseDataAvailable()
        {
            lock (_gate)
            {
                if (Volatile.Read(ref _isOpen) != 1 || _driverBuffer.Count == 0)
                {
                    return;
                }
            }

            DataAvailable?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DuCom.SessionCloseOrderTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, true);
            }
        }
    }
}
