using System.Text;
using DuCom.Core.Diagnostics;
using DuCom.Core.Logging;
using DuCom.Core.Parsing;
using DuCom.Core.Ports;
using DuCom.Core.Sessions;
using Xunit;

namespace DuCom.Core.Tests.Diagnostics;

/// <summary>
/// Close-gate completeness checks: written bytes, actual log files, close results, and
/// session faults must all be verified before a run can be reported Complete.
/// </summary>
public sealed class LoadCompletenessCloseGateTests
{
    [Fact]
    public void ZeroWrittenBytesWithPositiveInputIsIncomplete()
    {
        PipelineMetricsSnapshot pipeline = Snapshot(writtenBytes: 0);

        LoadCompletenessInfo info = LoadCompletenessEvaluator.Evaluate(pipeline, 10, 1_000, CleanGate(actualLogBytes: 0));

        Assert.False(info.IsComplete);
        Assert.Contains("written log bytes 0", info.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingLogFileIsIncomplete()
    {
        PipelineMetricsSnapshot pipeline = Snapshot(writtenBytes: 1_200);

        LoadCompletenessInfo info = LoadCompletenessEvaluator.Evaluate(
            pipeline, 10, 1_000, new SessionCloseGate(AllSessionsClosedCleanly: true, SessionFaultMessage: null, LogFilesExist: false, ActualLogBytes: 0));

        Assert.False(info.IsComplete);
        Assert.Contains("log files missing", info.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public void FileSizeMismatchIsIncomplete()
    {
        PipelineMetricsSnapshot pipeline = Snapshot(writtenBytes: 1_200);

        LoadCompletenessInfo info = LoadCompletenessEvaluator.Evaluate(pipeline, 10, 1_000, CleanGate(actualLogBytes: 1_199));

        Assert.False(info.IsComplete);
        Assert.Contains("actual log file bytes 1199 != written log bytes 1200", info.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public void CloseFaultIsIncomplete()
    {
        PipelineMetricsSnapshot pipeline = Snapshot(writtenBytes: 1_200);

        LoadCompletenessInfo info = LoadCompletenessEvaluator.Evaluate(
            pipeline, 10, 1_000, new SessionCloseGate(AllSessionsClosedCleanly: false, SessionFaultMessage: null, LogFilesExist: true, ActualLogBytes: 1_200));

        Assert.False(info.IsComplete);
        Assert.Contains("close did not complete cleanly", info.FailureReason, StringComparison.Ordinal);
        Assert.False(info.CloseSucceeded);
    }

    [Fact]
    public void SessionFaultIsIncomplete()
    {
        PipelineMetricsSnapshot pipeline = Snapshot(writtenBytes: 1_200);

        LoadCompletenessInfo info = LoadCompletenessEvaluator.Evaluate(
            pipeline, 10, 1_000, new SessionCloseGate(AllSessionsClosedCleanly: true, SessionFaultMessage: "ReceivePipeline: boom", LogFilesExist: true, ActualLogBytes: 1_200));

        Assert.False(info.IsComplete);
        Assert.Contains("session fault: ReceivePipeline: boom", info.FailureReason, StringComparison.Ordinal);
        Assert.False(info.SessionFaultFree);
    }

    [Fact]
    public void SuccessfulRotationAcrossMultipleFilesIsCompleteWhenBytesMatch()
    {
        PipelineMetricsSnapshot pipeline = Snapshot(writtenBytes: 1_200);

        LoadCompletenessInfo info = LoadCompletenessEvaluator.Evaluate(pipeline, 10, 1_000, CleanGate(actualLogBytes: 1_200));

        Assert.True(info.IsComplete);
        Assert.Equal(string.Empty, info.FailureReason);
        Assert.True(info.LogFilesExist);
        Assert.Equal(1_200, info.ActualLogBytes);
    }

    [Fact]
    public void WithoutCloseGateTheEvaluatorKeepsItsOriginalBehavior()
    {
        PipelineMetricsSnapshot pipeline = Snapshot(writtenBytes: 0);

        LoadCompletenessInfo info = LoadCompletenessEvaluator.Evaluate(pipeline, 10, 1_000);

        // The in-memory harness has no writer; the gate-only checks must not affect it.
        Assert.True(info.IsComplete);
    }

    [Fact]
    public async Task RotatedSessionProducesMultipleFilesWhoseBytesMatchWrittenLogBytes()
    {
        using TemporaryDirectory directory = new();
        RecordingTransport transport = new();
        SerialSession session = new(
            transport,
            SerialPortSettings.Default(transport.Settings.PortName),
            ReceiveDisplayMode.Str,
            timestampEnabled: false,
            new SessionLogWriterOptions(directory.Path, transport.Settings.PortName, RotationBytes: 64),
            lineBudgetBytes: 1024 * 1024);
        await session.OpenAsync();

        string line = new('a', 40);
        for (int index = 0; index < 12; index++)
        {
            transport.Receive(Encoding.UTF8.GetBytes(line + "\r\n"));
        }

        await session.CloseAsync();
        await session.DisposeAsync();

        string[] files = Directory.GetFiles(directory.Path, "*.txt");
        Assert.True(files.Length >= 3, $"expected rotation across multiple files, got {files.Length}");
        long actualBytes = files.Sum(path => new FileInfo(path).Length);
        PipelineMetricsSnapshot metrics = session.Snapshot().Metrics;
        Assert.True(metrics.WrittenLogBytes > 0);
        Assert.Equal(metrics.WrittenLogBytes, actualBytes);

        SessionCloseGate gate = new(true, null, files.Length > 0, actualBytes);
        LoadCompletenessInfo info = LoadCompletenessEvaluator.Evaluate(metrics, generatorInputBlocks: 12, generatorInputBytes: metrics.AcceptedBytes, gate);
        Assert.True(info.IsComplete);
    }

    [Fact]
    public void MarkdownReportShowsCloseGateFields()
    {
        PipelineMetricsSnapshot pipeline = Snapshot(writtenBytes: 1_200);
        LoadReport report = new(
            LoadReport.CurrentSchemaVersion,
            "report",
            DateTimeOffset.UtcNow,
            "worktree",
            new MachineInfo("m", "os", "runtime", 8),
            new LoadScenarioInfo("scenario", 1, 1, TimeSpan.FromSeconds(1), 2, 1_000_000, "mixed", "uniform"),
            pipeline,
            new ProcessMetricsSnapshot(TimeSpan.FromSeconds(1), 1, 1, 0, 0, 0, 1, 1, 1, 1, 1, 1, TimeSpan.Zero, 4),
            null,
            LoadCompletenessEvaluator.Evaluate(pipeline, 10, 1_000, CleanGate(1_200)));

        string markdown = LoadReportSerializer.ToMarkdown(report);

        Assert.Contains("Actual log file bytes", markdown, StringComparison.Ordinal);
        Assert.Contains("Session close", markdown, StringComparison.Ordinal);
        Assert.Contains("Session fault", markdown, StringComparison.Ordinal);
        Assert.Contains("Log files exist", markdown, StringComparison.Ordinal);
    }

    private static PipelineMetricsSnapshot Snapshot(long writtenBytes) => new(
        ProducedBlocks: 10,
        ProducedBytes: 1_000,
        AcceptedBlocks: 10,
        AcceptedBytes: 1_000,
        FormattedLogBlocks: 10,
        WrittenLogRecords: 10,
        WrittenLogBytes: writtenBytes,
        LineRecords: 10,
        Evictions: 0,
        ReceiveQueuePeak: 2,
        LogQueuePeak: 2,
        Faults: 0,
        ShutdownDrainState.Completed,
        TimeSpan.FromMilliseconds(5));

    private static SessionCloseGate CleanGate(long actualLogBytes) =>
        new(AllSessionsClosedCleanly: true, SessionFaultMessage: null, LogFilesExist: true, ActualLogBytes: actualLogBytes);

    private sealed class RecordingTransport : ISerialTransport
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<byte[]> _received = new();

        public event EventHandler? DataAvailable;

        public event EventHandler<TransportDisconnectedEventArgs>? Disconnected
        {
            add { }
            remove { }
        }

        public SerialPortSettings Settings { get; private set; } = SerialPortSettings.Default("ROT");

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

        public void Receive(byte[] payload)
        {
            _received.Enqueue(payload);
            DataAvailable?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory() => Path = Directory.CreateTempSubdirectory("ducom-close-gate-").FullName;

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
