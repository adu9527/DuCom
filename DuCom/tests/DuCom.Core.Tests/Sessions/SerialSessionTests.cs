using System.Collections.Concurrent;
using System.Text;
using DuCom.Core.Logging;
using DuCom.Core.Parsing;
using DuCom.Core.Ports;
using DuCom.Core.Sending;
using DuCom.Core.Sessions;
using DuCom.Core.Storage;

namespace DuCom.Core.Tests.Sessions;

public sealed class SerialSessionTests
{
    [Fact]
    public async Task ReceiveReachesLogAndLineStore()
    {
        using TemporaryDirectory directory = new();
        FakeSerialTransport transport = new("COM3");
        await using SerialSession session = CreateSession(transport, directory.Path);
        Assert.Equal(PortCommandResult.Succeeded, await session.OpenAsync());

        transport.Receive("hello\r\n"u8.ToArray());
        await WaitUntilAsync(() => session.Snapshot().Metrics.AcceptedBlocks == 1);
        Assert.Equal(PortCommandResult.Succeeded, await session.CloseAsync());

        SerialSessionSnapshot snapshot = session.Snapshot();
        StoredLine line = Assert.Single(snapshot.Lines.Lines);
        Assert.Equal(LineDirection.Rx, line.Direction);
        Assert.Equal("hello", line.Text);
        Assert.Equal("hello\r\n\r\n", ReadLogs(directory.Path));
        Assert.True(snapshot.Metrics.IsLogFormattingCoverageComplete);
        Assert.Null(snapshot.Fault);
    }

    [Fact]
    public async Task CloseFlushesUnterminatedReceiveLine()
    {
        using TemporaryDirectory directory = new();
        FakeSerialTransport transport = new("COM4");
        await using SerialSession session = CreateSession(transport, directory.Path);
        await session.OpenAsync();

        transport.Receive("partial"u8.ToArray());
        await WaitUntilAsync(() => session.Snapshot().Metrics.WrittenLogRecords == 1);
        Assert.Equal("partial", Assert.Single(session.Snapshot().Lines.Lines).Text);
        await WaitUntilAsync(() => ReadLogsShared(directory.Path) == "partial");
        Assert.Equal("partial", ReadLogsShared(directory.Path));
        await session.CloseAsync();

        StoredLine line = Assert.Single(session.Snapshot().Lines.Lines);
        Assert.Equal("partial", line.Text);
        Assert.False(line.IsTerminated);
        Assert.Equal("partial\r\n", ReadLogs(directory.Path));
    }

    [Fact]
    public async Task LongUnterminatedReceiveIsSoftWrappedButLogRemainsContinuous()
    {
        using TemporaryDirectory directory = new();
        FakeSerialTransport transport = new("COM-LONG");
        await using SerialSession session = CreateSession(transport, directory.Path);
        await session.OpenAsync();
        string payload = new('x', 10_000);

        transport.Receive(Encoding.UTF8.GetBytes(payload));
        await WaitUntilAsync(() => session.Snapshot().Metrics.AcceptedBlocks == 1);
        await session.CloseAsync();

        SerialSessionSnapshot snapshot = session.Snapshot();
        Assert.True(snapshot.Lines.Lines.Count >= 3);
        Assert.Single(snapshot.Lines.Lines.Select(line => line.LogicalId).Distinct());
        Assert.Equal(payload + "\r\n", ReadLogs(directory.Path));
        Assert.True(snapshot.Metrics.IsLogFormattingCoverageComplete);
    }

    [Fact]
    public async Task StringAndHexSendsWriteAndRecordTx()
    {
        using TemporaryDirectory directory = new();
        FakeSerialTransport transport = new("COM5");
        await using SerialSession session = CreateSession(transport, directory.Path);
        await session.OpenAsync();

        await session.SendAsync(SendMode.Str, "ping", NewlinePolicy.CrLf);
        await session.SendAsync(SendMode.Hex, "a0 0b", NewlinePolicy.None);
        await session.CloseAsync();

        Assert.Equal(["70696E670D0A", "A00B"], transport.Writes.Select(Convert.ToHexString));
        Assert.Equal(
            ["TX > ping", "TX > A0 0B"],
            session.Snapshot().Lines.Lines.Select(line => line.Text));
        Assert.Equal("TX > ping\r\nTX > A0 0B\r\n\r\n", ReadLogs(directory.Path));
    }

    [Fact]
    public async Task FailedTransportWriteDoesNotRecordTx()
    {
        using TemporaryDirectory directory = new();
        FakeSerialTransport transport = new("COM6") { WriteException = new IOException("write failed") };
        await using SerialSession session = CreateSession(transport, directory.Path);
        await session.OpenAsync();

        await Assert.ThrowsAsync<IOException>(async () =>
            await session.SendAsync(SendMode.Str, "lost", NewlinePolicy.None));
        await session.CloseAsync();

        Assert.Empty(session.Snapshot().Lines.Lines);
        Assert.Equal("\r\n", ReadLogs(directory.Path));
    }

    [Fact]
    public async Task SessionsKeepTransportLinesAndLogsIsolated()
    {
        using TemporaryDirectory firstDirectory = new();
        using TemporaryDirectory secondDirectory = new();
        FakeSerialTransport firstTransport = new("COM7");
        FakeSerialTransport secondTransport = new("COM8");
        await using SerialSession first = CreateSession(firstTransport, firstDirectory.Path);
        await using SerialSession second = CreateSession(secondTransport, secondDirectory.Path);
        await first.OpenAsync();
        await second.OpenAsync();

        firstTransport.Receive("first\n"u8.ToArray());
        secondTransport.Receive("second\n"u8.ToArray());
        await WaitUntilAsync(() => first.Snapshot().Metrics.AcceptedBlocks == 1);
        await WaitUntilAsync(() => second.Snapshot().Metrics.AcceptedBlocks == 1);
        await first.CloseAsync();
        await second.CloseAsync();

        Assert.Equal("first", Assert.Single(first.Snapshot().Lines.Lines).Text);
        Assert.Equal("second", Assert.Single(second.Snapshot().Lines.Lines).Text);
        Assert.Equal("first\r\n\r\n", ReadLogs(firstDirectory.Path));
        Assert.Equal("second\r\n\r\n", ReadLogs(secondDirectory.Path));
    }

    [Fact]
    public async Task LogStartFailureIsAnExplicitSessionFault()
    {
        using TemporaryDirectory directory = new();
        string filePath = Path.Combine(directory.Path, "not-a-directory");
        await File.WriteAllTextAsync(filePath, "x");
        FakeSerialTransport transport = new("COM9");
        await using SerialSession session = CreateSession(transport, filePath);

        Assert.Equal(PortCommandResult.Faulted, await session.OpenAsync());

        SerialSessionSnapshot snapshot = session.Snapshot();
        Assert.Equal(PortLifecycleState.Closed, snapshot.State.State);
        Assert.NotNull(snapshot.Fault);
        Assert.Equal("SessionLogWriter", snapshot.Fault.Source);
        Assert.Contains("not-a-directory", snapshot.Fault.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, transport.OpenCount);
    }

    [Fact]
    public async Task TransportOpenFailureRollsBackPipelineAndLogWriter()
    {
        using TemporaryDirectory directory = new();
        FakeSerialTransport transport = new("COM11") { OpenException = new IOException("open failed") };
        await using SerialSession session = CreateSession(transport, directory.Path);

        Assert.Equal(PortCommandResult.Faulted, await session.OpenAsync());

        SerialSessionSnapshot snapshot = session.Snapshot();
        Assert.Equal(PortLifecycleState.Closed, snapshot.State.State);
        Assert.Contains("open failed", snapshot.Fault?.Message);
        Assert.Equal(1, transport.OpenCount);
        Assert.Empty(Directory.GetFiles(directory.Path, "*.txt"));
    }

    [Fact]
    public async Task RuntimeLogFailureIsVisibleInSessionSnapshot()
    {
        using TemporaryDirectory directory = new();
        FakeSerialTransport transport = new("COM12");
        await using SerialSession session = CreateSession(transport, directory.Path);
        await session.OpenAsync();
        Directory.Delete(directory.Path, true);

        transport.Receive("cannot-log\n"u8.ToArray());
        await WaitUntilAsync(() => session.Snapshot().Fault is not null);
        await session.CloseAsync();

        SerialSessionSnapshot snapshot = session.Snapshot();
        Assert.Equal("SessionLogWriter", snapshot.Fault?.Source);
        Assert.True(snapshot.Metrics.Faults > 0);
    }

    [Fact]
    public async Task DisposeIsIdempotent()
    {
        using TemporaryDirectory directory = new();
        FakeSerialTransport transport = new("COM10");
        SerialSession session = CreateSession(transport, directory.Path);
        await session.OpenAsync();

        await session.DisposeAsync();
        await session.DisposeAsync();

        Assert.Equal(1, transport.DisposeCount);
        Assert.Equal(1, transport.CloseCount);
    }

    [Fact]
    public async Task ApplySettingsAsync_UpdatesOpenTransportWithoutClosingSession()
    {
        using TemporaryDirectory directory = new();
        FakeSerialTransport transport = new("COM13");
        await using SerialSession session = CreateSession(transport, directory.Path);
        await session.OpenAsync();
        SerialPortSettings updated = transport.Settings with { BaudRate = 3_000_000, DataBits = 7 };

        await session.ApplySettingsAsync(updated);

        Assert.Equal(updated, transport.Settings);
        Assert.Equal(0, transport.CloseCount);
        Assert.Equal(PortLifecycleState.Open, session.Status().State.State);
    }

    [Fact]
    public async Task ApplySettingsEncodingChangeKeepsQueuedBlocksOnCapturedEncoding()
    {
        using TemporaryDirectory directory = new();
        FakeSerialTransport transport = new("COM-PROFILE");
        await using SerialSession session = CreateSession(transport, directory.Path);
        await session.OpenAsync();

        transport.Receive([0xC3]);
        await WaitUntilAsync(() => session.Snapshot().Metrics.AcceptedBlocks == 1);
        await session.ApplySettingsAsync(session.Settings with { EncodingName = Encoding.Latin1.WebName });
        transport.Receive([0xE9, (byte)'\n']);
        await WaitUntilAsync(() => session.Snapshot().Metrics.AcceptedBlocks == 2);
        await session.CloseAsync();

        Assert.Equal("\uFFFDé\r\n\r\n", ReadLogs(directory.Path));
        Assert.Equal(2, session.Snapshot().Metrics.FormattedLogBlocks);
    }

    private static SerialSession CreateSession(FakeSerialTransport transport, string logDirectory)
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

    private static string ReadLogsShared(string directory) => string.Concat(
        Directory.GetFiles(directory, "*.txt").Order().Select(path =>
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using StreamReader reader = new(stream);
            return reader.ReadToEnd();
        }));

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class FakeSerialTransport(string portName) : ISerialTransport, ISerialSettingsTransport
    {
        private readonly ConcurrentQueue<byte[]> _received = new();

        public event EventHandler? DataAvailable;

        public event EventHandler<TransportDisconnectedEventArgs>? Disconnected
        {
            add { }
            remove { }
        }

        public SerialPortSettings Settings { get; private set; } = SerialPortSettings.Default(portName);

        public int BytesAvailable => _received.TryPeek(out byte[]? payload) ? payload.Length : 0;

        public List<byte[]> Writes { get; } = [];

        public Exception? WriteException { get; init; }

        public Exception? OpenException { get; init; }

        public int OpenCount { get; private set; }

        public int CloseCount { get; private set; }

        public int DisposeCount { get; private set; }

        public ValueTask OpenAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OpenCount++;
            if (OpenException is not null)
            {
                throw OpenException;
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask CloseAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CloseCount++;
            return ValueTask.CompletedTask;
        }

        public int Read(Span<byte> destination)
        {
            Assert.True(_received.TryDequeue(out byte[]? payload));
            payload.CopyTo(destination);
            return payload.Length;
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (WriteException is not null)
            {
                throw WriteException;
            }

            Writes.Add(data.ToArray());
            return ValueTask.CompletedTask;
        }

        public void ApplySettings(SerialPortSettings settings)
        {
            if (!string.Equals(settings.PortName, Settings.PortName, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Port name cannot change.");
            }

            Settings = settings;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }

        public void Receive(byte[] payload)
        {
            _received.Enqueue(payload);
            DataAvailable?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DuCom.SessionTests", Guid.NewGuid().ToString("N"));
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
