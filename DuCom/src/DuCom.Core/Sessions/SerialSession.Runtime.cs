using System.Buffers;
using DuCom.Core.Diagnostics;
using DuCom.Core.Logging;
using DuCom.Core.Parsing;
using DuCom.Core.Pipeline;
using DuCom.Core.Ports;
using DuCom.Core.Storage;

namespace DuCom.Core.Sessions;

public sealed partial class SerialSession
{
    private SessionRuntime CreateRuntime()
    {
        SessionLogWriter logWriter = new(_logOptions, _metrics);
        ReceiveFormattingProfile formattingProfile = CreateFormattingProfile(_settings.EncodingName, _formattingProfileVersion);
        ReceiveSessionSink sink = new(logWriter, _lineStore, _metrics, _displayTaps, _rawTaps);
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
        TimestampFormat: _timestampFormat,
        UnterminatedLineIdleMilliseconds: 200);

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
