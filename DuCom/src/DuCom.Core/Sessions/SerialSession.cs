using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using DuCom.Core.Diagnostics;
using DuCom.Core.Logging;
using DuCom.Core.Parsing;
using DuCom.Core.Pipeline;
using DuCom.Core.Ports;
using DuCom.Core.Sending;
using DuCom.Core.Storage;

namespace DuCom.Core.Sessions;

public sealed class SerialSession : IAsyncDisposable
{
    private const int DefaultMaximumSegmentCharacters = 16 * 1024;
    private const int DefaultReceiveCapacity = 256;
    private const int DefaultMaximumReadSize = 16 * 1024;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly object _disposeGate = new();
    private readonly ISerialTransport _transport;
    private SerialPortSettings _settings;
    private readonly ReceiveDisplayMode _receiveDisplayMode;
    private readonly bool _timestampEnabled;
    private readonly string _timestampFormat;
    private long _formattingProfileVersion;
    private readonly SessionLogWriterOptions _logOptions;
    private readonly bool _sendPrefixEnabled;
    private readonly string _sendPrefix;
    private readonly LoadMetrics _metrics = new();
    private readonly PortLifecycle _lifecycle;
    private readonly BudgetedLineStore _lineStore;
    private readonly SessionTapHub _displayTaps = new();
    private SessionRuntime? _runtime;
    private SessionFaultSnapshot? _fault;
    private Task? _disposeTask;
    private int _disposed;

    public SerialSession(
        ISerialTransport transport,
        SerialPortSettings settings,
        ReceiveDisplayMode receiveDisplayMode,
        bool timestampEnabled,
        SessionLogWriterOptions logOptions,
        int lineBudgetBytes,
        bool sendPrefixEnabled = true,
        string sendPrefix = "TX > ",
        string timestampFormat = "HH:mm:ss.fff")
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        settings.Validate();
        if (!Enum.IsDefined(receiveDisplayMode))
        {
            throw new ArgumentOutOfRangeException(nameof(receiveDisplayMode));
        }

        logOptions.Validate();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(lineBudgetBytes);
        _settings = settings;
        _receiveDisplayMode = receiveDisplayMode;
        _timestampEnabled = timestampEnabled;
        ArgumentException.ThrowIfNullOrWhiteSpace(timestampFormat);
        _timestampFormat = timestampFormat;
        _logOptions = logOptions;
        _sendPrefixEnabled = sendPrefixEnabled;
        _sendPrefix = sendPrefix ?? string.Empty;
        _lineStore = new BudgetedLineStore(lineBudgetBytes, DefaultMaximumSegmentCharacters);
        _lifecycle = new PortLifecycle(settings.PortName, transport);
        _transport.Disconnected += OnTransportDisconnected;
    }

    public SerialSessionSnapshot Snapshot()
    {
        SessionRuntime? runtime = Volatile.Read(ref _runtime);
        SessionFaultSnapshot? fault = Volatile.Read(ref _fault)
            ?? CreateFault("Lifecycle", _lifecycle.Snapshot.FaultMessage)
            ?? CreateFault("ReceivePipeline", runtime?.Pipeline.Fault)
            ?? CreateFault("SessionLogWriter", runtime?.LogWriter.Fault);
        return new SerialSessionSnapshot(
            _lifecycle.Snapshot,
            _lineStore.Snapshot(),
            _metrics.Snapshot(),
            fault);
    }

    public SerialSessionStatusSnapshot Status()
    {
        SerialSessionSnapshot snapshot = SnapshotWithoutLines();
        return new SerialSessionStatusSnapshot(snapshot.State, snapshot.Metrics, snapshot.Fault);
    }

    public LineStoreSnapshot GetLinesAfter(LineCursor? cursor, int maximumSegments = 2_048) =>
        _lineStore.SnapshotAfter(cursor, maximumSegments);

    public void ClearDisplay() => _lineStore.Clear();

    /// <summary>Display tap fan-out for auxiliary surfaces (float send window, log filter).</summary>
    public SessionTapHub DisplayTaps => _displayTaps;

    public SerialPortSettings Settings => _settings;

    public string LogDirectory => Volatile.Read(ref _runtime)?.LogWriter.OutputDirectory
        ?? _logOptions.GetOutputDirectory(DateTimeOffset.Now);

    public string? CurrentLogFilePath => Volatile.Read(ref _runtime)?.LogWriter.CurrentFilePath;

    public async Task ApplySettingsAsync(SerialPortSettings settings, CancellationToken cancellationToken = default)
    {
        settings.Validate();
        if (!string.Equals(settings.PortName, _settings.PortName, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Port name cannot change while updating a serial session.", nameof(settings));
        }

        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_transport is not ISerialSettingsTransport configurable)
            {
                // ADR-0004: in-place settings updates are a COM-transport capability, not
                // part of the transport-neutral contract.
                throw new NotSupportedException("This transport does not support in-place serial settings updates.");
            }

            SerialPortSettings previous = _settings;
            bool encodingChanged = _runtime is not null &&
                !string.Equals(settings.EncodingName, _settings.EncodingName, StringComparison.OrdinalIgnoreCase);
            ReceiveFormattingProfile? replacementProfile = encodingChanged
                ? CreateFormattingProfile(settings.EncodingName, _formattingProfileVersion + 1)
                : null;

            try
            {
                configurable.ApplySettings(settings);
                if (replacementProfile is not null)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    _runtime!.Pipeline.UpdateFormattingProfile(replacementProfile);
                    _formattingProfileVersion = replacementProfile.Version;
                }

                _settings = settings;
            }
            catch (Exception failure)
            {
                try
                {
                    configurable.ApplySettings(previous);
                }
                catch (Exception rollbackFailure)
                {
                    throw new AggregateException(
                        "Applying serial settings failed and restoring the previous settings also failed. The port may be in a mixed configuration; reopen the port to recover.",
                        failure,
                        rollbackFailure);
                }

                throw;
            }
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private SerialSessionSnapshot SnapshotWithoutLines()
    {
        SessionRuntime? runtime = Volatile.Read(ref _runtime);
        SessionFaultSnapshot? fault = Volatile.Read(ref _fault)
            ?? CreateFault("Lifecycle", _lifecycle.Snapshot.FaultMessage)
            ?? CreateFault("ReceivePipeline", runtime?.Pipeline.Fault)
            ?? CreateFault("SessionLogWriter", runtime?.LogWriter.Fault);
        return new SerialSessionSnapshot(
            _lifecycle.Snapshot,
            new LineStoreSnapshot(null, null, 0, []),
            _metrics.Snapshot(),
            fault);
    }

    public async Task<PortCommandResult> OpenAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return PortCommandResult.Disposed;
        }

        try
        {
            await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return PortCommandResult.Cancelled;
        }

        try
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return PortCommandResult.Disposed;
            }

            if (_lifecycle.Snapshot.State != PortLifecycleState.Closed)
            {
                return PortCommandResult.AlreadyOpen;
            }

            SessionRuntime? previousRuntime = Volatile.Read(ref _runtime);
            if (previousRuntime is not null)
            {
                try
                {
                    await DrainRuntimeAsync(previousRuntime).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    SetFault("ShutdownDrain", exception);
                    return PortCommandResult.Faulted;
                }

                CaptureRuntimeFault(previousRuntime);
                Interlocked.CompareExchange(ref _runtime, null, previousRuntime);
            }

            SessionRuntime runtime = CreateRuntime();
            Volatile.Write(ref _runtime, runtime);
            Volatile.Write(ref _fault, null);
            if (Volatile.Read(ref _disposed) != 0)
            {
                // Disposal set its flag between the in-lock check above and this publish,
                // and may have captured a null runtime reference before taking the lock.
                // Roll the freshly published runtime back; disposal re-reads the runtime
                // under this lock, sees it, and its idempotent drain completes the cleanup
                // (Open/Dispose publication race, 2026-08-28 review).
                await RollbackOpenAsync(runtime).ConfigureAwait(false);
                CaptureRuntimeFault(runtime);
                return PortCommandResult.Disposed;
            }

            try
            {
                await runtime.LogWriter.StartAsync().ConfigureAwait(false);
                if (runtime.LogWriter.Fault is not null)
                {
                    throw new IOException("Session log writer failed to start.", runtime.LogWriter.Fault);
                }

                await runtime.Pipeline.StartAsync().ConfigureAwait(false);
                PortCommandResult result = await _lifecycle.OpenAsync(cancellationToken).ConfigureAwait(false);
                if (result == PortCommandResult.Succeeded)
                {
                    return result;
                }

                await RollbackOpenAsync(runtime).ConfigureAwait(false);
                CaptureRuntimeFault(runtime);
                return result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await RollbackOpenAsync(runtime).ConfigureAwait(false);
                return PortCommandResult.Cancelled;
            }
            catch (Exception exception)
            {
                SetFault("Open", exception);
                await RollbackOpenAsync(runtime).ConfigureAwait(false);
                CaptureRuntimeFault(runtime);
                return PortCommandResult.Faulted;
            }
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task<PortCommandResult> CloseAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return PortCommandResult.Disposed;
        }

        Task? faultHandlingTask = Volatile.Read(ref _runtime)?.FaultHandlingTask;
        if (faultHandlingTask is not null)
        {
            await faultHandlingTask.ConfigureAwait(false);
            return PortCommandResult.AlreadyClosed;
        }

        try
        {
            await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return PortCommandResult.Cancelled;
        }

        try
        {
            return await CloseCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async ValueTask SendAsync(
        SendMode mode,
        string text,
        NewlinePolicy newline,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_lifecycle.Snapshot.State != PortLifecycleState.Open || _runtime is null)
            {
                throw new InvalidOperationException("Serial session must be open before sending.");
            }

            Encoding encoding = Encoding.GetEncoding(_settings.EncodingName);
            byte[] payload = mode switch
            {
                SendMode.Str => SendPayloadEncoder.EncodeString(text, encoding, newline),
                SendMode.Hex => SendPayloadEncoder.EncodeHex(text, newline),
                _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
            };

            await _transport.WriteAsync(payload, cancellationToken).ConfigureAwait(false);

            _displayTaps.NotifySent(mode);
            string displayText = mode == SendMode.Str ? text : FormatHex(payload);
            string recordText = _sendPrefixEnabled ? _sendPrefix + displayText : displayText;
            if (!await _runtime.LogWriter.WriteAsync(
                    new FormattedLogRecord(recordText + "\r\n"),
                    CancellationToken.None).ConfigureAwait(false))
            {
                IOException exception = new("Session log writer rejected a transmitted record.", _runtime.LogWriter.Fault);
                SetFault("SessionLogWriter", exception);
                throw exception;
            }

            _lineStore.Append(LineDirection.Tx, DateTimeOffset.UtcNow, recordText, isTerminated: true);
            _displayTaps.PublishTransmit(recordText);
            _metrics.AddLineRecords(1);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    /// <summary>
    /// Idempotent and concurrency-safe disposal: concurrent callers share one disposal task.
    /// Runs the exact ADR-0004 close order — quiesce+drain receive while the transport is
    /// open, close the transport, then formatter flush, log-Channel drain, file flush, and
    /// runtime disposal — attempting every later step even when an earlier one fails.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
        {
            _disposeTask ??= DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        Volatile.Write(ref _disposed, 1);
        _transport.Disconnected -= OnTransportDisconnected;
        SessionRuntime? earlyRuntime = Volatile.Read(ref _runtime);
        Task? faultHandlingTask = DetachFaultHandling(earlyRuntime);

        await _operationLock.WaitAsync().ConfigureAwait(false);
        List<Exception> failures = [];
        try
        {
            // Re-read the runtime under the operation lock: an Open that passed its own
            // disposed check and published the runtime while disposal waited for the lock
            // is visible here and must be drained — the early read above may have seen
            // null in that race (Open/Dispose publication race, 2026-08-28 review).
            SessionRuntime? runtime = Volatile.Read(ref _runtime) ?? earlyRuntime;

            // Step 1 (ADR-0004): quiesce receive callbacks and drain the driver buffer
            // while the transport is still open.
            if (runtime is not null)
            {
                try
                {
                    await runtime.Pipeline.StopAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    SetFault("ReceivePipeline", exception);
                    failures.Add(exception);
                }

                if (runtime.Pipeline.Fault is not null)
                {
                    SetFault("ReceivePipeline", runtime.Pipeline.Fault);
                }
            }

            // Step 2: close the transport before waiting on log-side drain so a slow log
            // flush never keeps the port open with nobody reading it.
            try
            {
                PortCommandResult closeResult = await _lifecycle.CloseAsync(CancellationToken.None).ConfigureAwait(false);
                if (closeResult == PortCommandResult.Faulted)
                {
                    Volatile.Write(ref _fault, CreateFault("Lifecycle", _lifecycle.Snapshot.FaultMessage));
                }
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }

            // Steps 3-5: formatter flush, log-Channel drain, file flush/dispose, runtime
            // disposal (pipeline stop is idempotent and shared inside DrainRuntimeAsync).
            if (runtime is not null)
            {
                try
                {
                    await DrainRuntimeAsync(runtime).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    SetFault("ShutdownDrain", exception);
                    failures.Add(exception);
                }

                CaptureRuntimeFault(runtime);
            }

            // Step 6: always dispose the lifecycle (and with it the transport), even when
            // an earlier step failed.
            try
            {
                await _lifecycle.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }
        finally
        {
            _operationLock.Release();
        }

        // The detached fault-handling task takes the operation lock itself, so it must be
        // awaited after releasing the lock but before disposing it — its work is idempotent
        // against the drain above (shared stop task, Drained flag, AlreadyClosed close).
        if (faultHandlingTask is not null)
        {
            try
            {
                await faultHandlingTask.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        _operationLock.Dispose();

        if (failures.Count > 0)
        {
            throw new AggregateException("One or more session disposal operations failed.", failures);
        }
    }

    private static Task? DetachFaultHandling(SessionRuntime? runtime)
    {
        if (runtime is null)
        {
            return null;
        }

        lock (runtime.FaultGate)
        {
            runtime.AcceptFaultHandling = false;
            return runtime.FaultHandlingTask;
        }
    }

    private SessionRuntime CreateRuntime()
    {
        SessionLogWriter logWriter = new(_logOptions, _metrics);
        ReceiveFormattingProfile formattingProfile = CreateFormattingProfile(_settings.EncodingName, _formattingProfileVersion);
        ReceiveSessionSink sink = new(logWriter, _lineStore, _metrics, _displayTaps);
        ReceivePipeline pipeline = new(
            _transport,
            sink,
            _metrics,
            ArrayPool<byte>.Shared,
            DefaultReceiveCapacity,
            DefaultMaximumReadSize,
            formattingProfile);
        SessionRuntime runtime = new(logWriter, sink, pipeline, _metrics);
        pipeline.Faulted += (_, exception) => OnRuntimeFault(runtime, exception);
        return runtime;
    }

    private ReceiveFormattingProfile CreateFormattingProfile(string encodingName, long version) => new(
        version,
        encodingName,
        _receiveDisplayMode,
        _timestampEnabled,
        TimestampFormat: _timestampFormat);

    private async Task<PortCommandResult> CloseCoreAsync(CancellationToken cancellationToken)
    {
        // ADR-0004: quiesce and drain the receive side while the transport is still open.
        // SerialPort discards its driver receive buffer when Close() runs, so draining after
        // the lifecycle close would silently lose every still-buffered byte.
        SessionRuntime? runtime = Volatile.Read(ref _runtime);
        bool faultedDuringClose = false;
        if (runtime is not null)
        {
            try
            {
                await runtime.Pipeline.StopAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                SetFault("ReceivePipeline", exception);
                faultedDuringClose = true;
            }

            if (runtime.Pipeline.Fault is not null)
            {
                SetFault("ReceivePipeline", runtime.Pipeline.Fault);
                faultedDuringClose = true;
            }
        }

        // Once the receive side is quiesced the close commits: honoring a cancellation here
        // would leave the lifecycle reporting Open over a drained, dead receive pipeline.
        PortCommandResult result = await _lifecycle.CloseAsync(CancellationToken.None).ConfigureAwait(false);
        if (result is not PortCommandResult.Succeeded and not PortCommandResult.AlreadyClosed)
        {
            if (result == PortCommandResult.Faulted)
            {
                Volatile.Write(ref _fault, CreateFault("Lifecycle", _lifecycle.Snapshot.FaultMessage));
            }

            if (runtime is not null)
            {
                try
                {
                    await DrainRuntimeAsync(runtime).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    SetFault("ShutdownDrain", exception);
                }

                CaptureRuntimeFault(runtime);
            }

            return result;
        }

        if (runtime is not null)
        {
            try
            {
                await DrainRuntimeAsync(runtime).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                SetFault("ShutdownDrain", exception);
                faultedDuringClose = true;
            }

            CaptureRuntimeFault(runtime);
            if (runtime.Pipeline.Fault is not null || runtime.LogWriter.Fault is not null)
            {
                faultedDuringClose = true;
            }
        }

        return faultedDuringClose ? PortCommandResult.Faulted : result;
    }

    private static async Task RollbackOpenAsync(SessionRuntime runtime)
    {
        try
        {
            await DrainRuntimeAsync(runtime).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private static async Task DrainRuntimeAsync(SessionRuntime runtime)
    {
        if (Interlocked.Exchange(ref runtime.Drained, 1) != 0)
        {
            return;
        }

        Stopwatch drain = Stopwatch.StartNew();
        List<Exception> failures = [];
        try
        {
            await TryCleanupAsync(runtime.Pipeline.StopAsync, failures).ConfigureAwait(false);
            await TryCleanupAsync(() => runtime.Sink.FlushAsync(CancellationToken.None), failures).ConfigureAwait(false);
            await TryCleanupAsync(runtime.LogWriter.StopAsync, failures).ConfigureAwait(false);
            await TryCleanupAsync(() => runtime.Pipeline.DisposeAsync().AsTask(), failures).ConfigureAwait(false);
            await TryCleanupAsync(() => runtime.Sink.DisposeAsync().AsTask(), failures).ConfigureAwait(false);
            await TryCleanupAsync(() => runtime.LogWriter.DisposeAsync().AsTask(), failures).ConfigureAwait(false);
        }
        finally
        {
            drain.Stop();
            runtime.Metrics.SetShutdownDrain(
                failures.Count == 0 && runtime.Pipeline.Fault is null && runtime.LogWriter.Fault is null
                    ? ShutdownDrainState.Completed
                    : ShutdownDrainState.Faulted,
                drain.Elapsed);
        }

        if (failures.Count > 0)
        {
            throw new AggregateException("One or more session drain operations failed.", failures);
        }
    }

    private void CaptureRuntimeFault(SessionRuntime runtime)
    {
        if (runtime.Pipeline.Fault is not null)
        {
            SetFault("ReceivePipeline", runtime.Pipeline.Fault);
        }
        else if (runtime.LogWriter.Fault is not null)
        {
            SetFault("SessionLogWriter", runtime.LogWriter.Fault);
        }
    }

    private void SetFault(string source, Exception exception) =>
        Volatile.Write(ref _fault, new SessionFaultSnapshot(source, exception.ToString()));

    private void OnRuntimeFault(SessionRuntime runtime, Exception exception)
    {
        SetFault("ReceivePipeline", exception);
        ScheduleRuntimeCleanup(runtime);
    }

    private void OnTransportDisconnected(object? sender, TransportDisconnectedEventArgs e)
    {
        SetFault("Lifecycle", e.Exception);
        SessionRuntime? runtime = Volatile.Read(ref _runtime);
        if (runtime is not null)
        {
            ScheduleRuntimeCleanup(runtime);
        }
    }

    private void ScheduleRuntimeCleanup(SessionRuntime runtime)
    {
        lock (runtime.FaultGate)
        {
            if (!runtime.AcceptFaultHandling || Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            runtime.FaultHandlingTask ??= Task.Run(async () =>
            {
                await _operationLock.WaitAsync().ConfigureAwait(false);
                try
                {
                    bool isCurrentRuntime = ReferenceEquals(Volatile.Read(ref _runtime), runtime);
                    if (isCurrentRuntime)
                    {
                        await _lifecycle.CloseAsync(CancellationToken.None).ConfigureAwait(false);
                    }

                    try
                    {
                        await DrainRuntimeAsync(runtime).ConfigureAwait(false);
                    }
                    catch (Exception drainException)
                    {
                        SetFault("ShutdownDrain", drainException);
                    }

                    if (ReferenceEquals(Volatile.Read(ref _runtime), runtime))
                    {
                        CaptureRuntimeFault(runtime);
                        Interlocked.CompareExchange(ref _runtime, null, runtime);
                    }
                }
                finally
                {
                    _operationLock.Release();
                }
            });
        }
    }

    private static async Task TryCleanupAsync(Func<Task> operation, List<Exception> failures)
    {
        try
        {
            await operation().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
    }

    private static SessionFaultSnapshot? CreateFault(string source, Exception? exception) =>
        exception is null ? null : new SessionFaultSnapshot(source, exception.Message);

    private static SessionFaultSnapshot? CreateFault(string source, string? message) =>
        string.IsNullOrWhiteSpace(message) ? null : new SessionFaultSnapshot(source, message);

    private static string FormatHex(ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty)
        {
            return string.Empty;
        }

        StringBuilder builder = new(payload.Length * 3 - 1);
        foreach (byte value in payload)
        {
            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            builder.Append(value.ToString("X2", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    private sealed class SessionRuntime(
        SessionLogWriter logWriter,
        ReceiveSessionSink sink,
        ReceivePipeline pipeline,
        LoadMetrics metrics)
    {
        public SessionLogWriter LogWriter { get; } = logWriter;

        public ReceiveSessionSink Sink { get; } = sink;

        public ReceivePipeline Pipeline { get; } = pipeline;

        public LoadMetrics Metrics { get; } = metrics;

        public int Drained;

        public Task? FaultHandlingTask;

        public object FaultGate { get; } = new();

        public bool AcceptFaultHandling { get; set; } = true;
    }
}
