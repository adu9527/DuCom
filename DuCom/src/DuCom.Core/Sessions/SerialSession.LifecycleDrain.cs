using System.Diagnostics;
using DuCom.Core.Diagnostics;
using DuCom.Core.Logging;
using DuCom.Core.Ports;
using DuCom.Core.Storage;

namespace DuCom.Core.Sessions;

public sealed partial class SerialSession
{
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
                await DrainRuntimeAsync(runtime, appendDisconnectNewline: true).ConfigureAwait(false);
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

    private static async Task DrainRuntimeAsync(SessionRuntime runtime, bool appendDisconnectNewline = false)
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
            if (appendDisconnectNewline)
            {
                await TryCleanupAsync(async () =>
                {
                    if (!await runtime.LogWriter.WriteAsync(
                            new FormattedLogRecord("\r\n", BypassRotation: true),
                            CancellationToken.None).ConfigureAwait(false))
                    {
                        throw new IOException("Session log writer rejected the disconnect newline.");
                    }
                }, failures).ConfigureAwait(false);
            }
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
}
