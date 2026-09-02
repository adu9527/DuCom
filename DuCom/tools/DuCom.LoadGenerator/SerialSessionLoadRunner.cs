using System.Collections.Concurrent;
using DuCom.Core.Diagnostics;
using DuCom.Core.Logging;
using DuCom.Core.Parsing;
using DuCom.Core.Ports;
using DuCom.Core.Sessions;

namespace DuCom.LoadGenerator;

/// <summary>Per-session close-gate evidence: input, close result, fault, and actual files.</summary>
internal sealed record SessionLoadGate(
    string PortName,
    long GeneratorInputBlocks,
    long GeneratorInputBytes,
    PortCommandResult CloseResult,
    string? FaultMessage,
    bool LogFilesExist,
    long ActualLogBytes,
    PipelineMetricsSnapshot Metrics);

internal sealed record SerialSessionLoadResult(
    PipelineMetricsSnapshot Metrics,
    TimeSpan Elapsed,
    string LogDirectory,
    string? FaultMessage,
    long GeneratorInputBlocks,
    long GeneratorInputBytes,
    bool AllSessionsClosedCleanly,
    IReadOnlyList<SessionLoadGate> PerSession)
{
    public string PerSessionSummary => string.Join(" | ", PerSession.Select(session =>
        $"{session.PortName}: input {session.GeneratorInputBlocks} blocks/{session.GeneratorInputBytes} bytes, " +
        $"produced {session.Metrics.ProducedBlocks}/{session.Metrics.ProducedBytes}, " +
        $"accepted {session.Metrics.AcceptedBlocks}/{session.Metrics.AcceptedBytes}, " +
        $"formatted {session.Metrics.FormattedLogBlocks}, written {session.Metrics.WrittenLogRecords}/{session.Metrics.WrittenLogBytes}, " +
        $"files {(session.LogFilesExist ? session.ActualLogBytes.ToString(System.Globalization.CultureInfo.InvariantCulture) : "missing")}, close {session.CloseResult}, fault {(session.FaultMessage is null ? "null" : "present")}"));
}

internal static class SerialSessionLoadRunner
{
    public static async Task<SerialSessionLoadResult> RunAsync(
        LoadGeneratorOptions options,
        string logDirectory,
        bool pace,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(logDirectory);
        InMemorySerialTransport[] transports = Enumerable.Range(0, options.PortCount)
            .Select(index => new InMemorySerialTransport($"LOAD{index + 1}"))
            .ToArray();
        string[] portNames = transports.Select(transport => transport.Settings.PortName).ToArray();
        SerialSession[] sessions = transports
            .Select(transport => new SerialSession(
                transport,
                transport.Settings,
                ReceiveDisplayMode.Str,
                timestampEnabled: false,
                new SessionLogWriterOptions(logDirectory, transport.Settings.PortName),
                lineBudgetBytes: 8 * 1024 * 1024))
            .ToArray();

        System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            foreach (SerialSession session in sessions)
            {
                PortCommandResult result = await session.OpenAsync(cancellationToken);
                if (result != PortCommandResult.Succeeded)
                {
                    throw new IOException($"Session open failed: {result}.");
                }
            }

            long[] inputBlocksPerPort = new long[options.PortCount];
            long[] inputBytesPerPort = new long[options.PortCount];
            foreach (GeneratedLoadBlock block in DeterministicLoadGenerator.Generate(options))
            {
                if (pace)
                {
                    TimeSpan delay = block.ScheduledOffset - stopwatch.Elapsed;
                    if (delay > TimeSpan.Zero)
                    {
                        await Task.Delay(delay, cancellationToken);
                    }
                }

                transports[block.PortIndex].Receive(block.Payload);
                inputBlocksPerPort[block.PortIndex]++;
                inputBytesPerPort[block.PortIndex] += block.Payload.Length;
            }

            PortCommandResult[] closeResults = new PortCommandResult[sessions.Length];
            for (int index = 0; index < sessions.Length; index++)
            {
                closeResults[index] = await sessions[index].CloseAsync(CancellationToken.None);
            }

            // Per-session metrics and faults are captured after close (drain complete).
            PipelineMetricsSnapshot[] sessionMetrics = sessions.Select(session => session.Snapshot().Metrics).ToArray();
            string?[] sessionFaults = sessions.Select(session => session.Snapshot().Fault?.Message).ToArray();

            return new SerialSessionLoadResult(
                Aggregate(sessionMetrics),
                stopwatch.Elapsed,
                logDirectory,
                sessionFaults.FirstOrDefault(message => message is not null),
                inputBlocksPerPort.Sum(),
                inputBytesPerPort.Sum(),
                closeResults.All(result => result is PortCommandResult.Succeeded or PortCommandResult.AlreadyClosed),
                [.. portNames.Select((portName, index) => new SessionLoadGate(
                    portName,
                    inputBlocksPerPort[index],
                    inputBytesPerPort[index],
                    closeResults[index],
                    sessionFaults[index],
                    LogFilesExist(logDirectory, portName),
                    GetActualLogBytes(logDirectory, portName),
                    sessionMetrics[index]))]);
        }
        finally
        {
            stopwatch.Stop();
            foreach (SerialSession session in sessions)
            {
                await session.DisposeAsync();
            }
        }
    }

    /// <summary>Sums the bytes of one session's log files ({Port}-*.txt) actually on disk.</summary>
    public static long GetActualLogBytes(string logDirectory, string sessionName) =>
        Directory.Exists(logDirectory)
            ? GetSessionFiles(logDirectory, sessionName).Sum(path => new FileInfo(path).Length)
            : 0;

    public static bool LogFilesExist(string logDirectory, string sessionName) =>
        Directory.Exists(logDirectory) && GetSessionFiles(logDirectory, sessionName).Length > 0;

    private static string[] GetSessionFiles(string logDirectory, string sessionName) =>
        Directory.GetFiles(logDirectory, $"{sessionName}-*.txt");

    private static PipelineMetricsSnapshot Aggregate(IEnumerable<PipelineMetricsSnapshot> snapshots)
    {
        PipelineMetricsSnapshot[] values = snapshots.ToArray();
        return new PipelineMetricsSnapshot(
            values.Sum(value => value.ProducedBlocks),
            values.Sum(value => value.ProducedBytes),
            values.Sum(value => value.AcceptedBlocks),
            values.Sum(value => value.AcceptedBytes),
            values.Sum(value => value.FormattedLogBlocks),
            values.Sum(value => value.WrittenLogRecords),
            values.Sum(value => value.WrittenLogBytes),
            values.Sum(value => value.LineRecords),
            values.Sum(value => value.Evictions),
            values.Max(value => value.ReceiveQueuePeak),
            values.Max(value => value.LogQueuePeak),
            values.Sum(value => value.Faults),
            values.All(value => value.ShutdownDrainState == ShutdownDrainState.Completed)
                ? ShutdownDrainState.Completed
                : ShutdownDrainState.Faulted,
            values.Max(value => value.ShutdownDrainDuration));
    }

    private sealed class InMemorySerialTransport(string portName) : ISerialTransport
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
}
