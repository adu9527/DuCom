using System.Diagnostics;
using DuCom.Core.Parsing;
using DuCom.Core.Ports;

namespace DuCom.Core.Pipeline;

public sealed partial class ReceivePipeline
{
    private static readonly TimeSpan DedicatedReadCoalescingWindow = TimeSpan.FromMilliseconds(4);

    private void RunDedicatedReceivePump(CancellationToken cancellationToken)
    {
        try
        {
            IDedicatedReceiveTransport transport = (IDedicatedReceiveTransport)_transport;
            transport.WaitUntilOpen(cancellationToken);

            while (!cancellationToken.IsCancellationRequested)
            {
                _capacitySlots.Wait(cancellationToken);
                byte[] buffer = _bufferPool.Rent(_maximumReadSize);
                int length;
                try
                {
                    length = transport.Read(buffer, 0, _maximumReadSize);
                    long coalescingDeadline = Stopwatch.GetTimestamp() +
                        (long)(DedicatedReadCoalescingWindow.TotalSeconds * Stopwatch.Frequency);
                    while (length < _maximumReadSize && !cancellationToken.IsCancellationRequested)
                    {
                        int available = _transport.BytesAvailable;
                        if (available <= 0)
                        {
                            if (Stopwatch.GetTimestamp() >= coalescingDeadline)
                            {
                                break;
                            }
                            Thread.Sleep(1);
                            continue;
                        }

                        int appended = transport.Read(buffer, length, Math.Min(available, _maximumReadSize - length));
                        if (appended <= 0)
                        {
                            break;
                        }
                        length += appended;
                    }
                }
                catch (TimeoutException)
                {
                    _bufferPool.Return(buffer);
                    _capacitySlots.Release();
                    continue;
                }
                catch
                {
                    _bufferPool.Return(buffer);
                    _capacitySlots.Release();
                    throw;
                }

                if (length <= 0)
                {
                    _bufferPool.Return(buffer);
                    _capacitySlots.Release();
                    continue;
                }

                _metrics.AddProducedBlock(length);
                ReceiveBlock block = new(
                    _bufferPool,
                    buffer,
                    length,
                    DateTimeOffset.UtcNow,
                    Volatile.Read(ref _formattingProfile));
                if (!_channel.Writer.TryWrite(block))
                {
                    block.Dispose();
                    _capacitySlots.Release();
                    throw new InvalidOperationException("Reserved receive capacity could not be transferred to the Channel.");
                }

                int queued = Interlocked.Increment(ref _queuedBlocks);
                _metrics.ObserveReceiveQueueDepth(queued);
                _metrics.AddAcceptedBlock(length);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (InvalidOperationException) when (_transport is IDedicatedReceiveTransport)
        {
            // Closing or unplugging the port can release a blocking SerialPort.Read by
            // making the handle unavailable. Lifecycle/disconnect reporting owns that state.
        }
        catch (Exception exception)
        {
            FaultPipeline(exception);
        }
    }

    private void OnDataAvailable(object? sender, EventArgs e)
    {
        Interlocked.Exchange(ref _dataAvailableRequested, 1);
        if (Interlocked.CompareExchange(ref _dataAvailableCallbackActive, 1, 0) != 0)
        {
            return;
        }

        Interlocked.Increment(ref _activeCallbacks);
        _callbacksIdle.Reset();
        bool ownsCallback = true;
        try
        {
            while (Volatile.Read(ref _stopping) == 0)
            {
                while (Interlocked.Exchange(ref _dataAvailableRequested, 0) != 0)
                {
                    ReceiveFormattingProfile formattingProfile = Volatile.Read(ref _formattingProfile);
                    long now = Stopwatch.GetTimestamp();
                    long previous = Interlocked.Exchange(ref _lastDataAvailableTimestamp, now);
                    double callbackGapMilliseconds = previous == 0
                        ? 0
                        : Stopwatch.GetElapsedTime(previous, now).TotalMilliseconds;
                    ReadAvailableIntoChannel(formattingProfile, "DataAvailable", callbackGapMilliseconds);
                }

                Volatile.Write(ref _dataAvailableCallbackActive, 0);
                ownsCallback = false;
                if (Volatile.Read(ref _dataAvailableRequested) == 0 ||
                    Interlocked.CompareExchange(ref _dataAvailableCallbackActive, 1, 0) != 0)
                {
                    break;
                }
                ownsCallback = true;
            }
        }
        catch (Exception exception)
        {
            FaultPipeline(exception);
        }
        finally
        {
            if (ownsCallback)
            {
                Volatile.Write(ref _dataAvailableCallbackActive, 0);
            }
            lock (_readGate)
            {
                if (Interlocked.Decrement(ref _activeCallbacks) == 0)
                {
                    _callbacksIdle.Set();
                }
            }
        }
    }

    private void ReadAvailableIntoChannel(
        ReceiveFormattingProfile? formattingProfile = null,
        string trigger = "BackpressureResume",
        double callbackGapMilliseconds = 0)
    {
        lock (_readGate)
        {
            formattingProfile ??= Volatile.Read(ref _formattingProfile);
            int initialBytesAvailable = _transport.BytesAvailable;
            long diagnosticBatchId = BeginDiagnosticBatch(trigger, callbackGapMilliseconds, initialBytesAvailable);
            int readBlocks = 0;
            int readBytes = 0;
            int maximumReadBytes = 0;
            int queueDepthPeak = Volatile.Read(ref _queuedBlocks);
            while (Volatile.Read(ref _stopping) == 0 && _transport.BytesAvailable > 0 && _capacitySlots.Wait(0))
            {
                int requested = Math.Min(_transport.BytesAvailable, _maximumReadSize);
                byte[] buffer = _bufferPool.Rent(requested);
                int length;
                try
                {
                    length = _transport.Read(buffer.AsSpan(0, requested));
                }
                catch
                {
                    _bufferPool.Return(buffer);
                    _capacitySlots.Release();
                    throw;
                }

                if (length <= 0)
                {
                    _bufferPool.Return(buffer);
                    _capacitySlots.Release();
                    break;
                }

                _metrics.AddProducedBlock(length);
                ReceiveBlock block = new(
                    _bufferPool,
                    buffer,
                    length,
                    DateTimeOffset.UtcNow,
                    formattingProfile,
                    diagnosticBatchId);
                if (!_channel.Writer.TryWrite(block))
                {
                    block.Dispose();
                    _capacitySlots.Release();
                    throw new InvalidOperationException("Reserved receive capacity could not be transferred to the Channel.");
                }

                int queued = Interlocked.Increment(ref _queuedBlocks);
                readBlocks++;
                readBytes += length;
                maximumReadBytes = Math.Max(maximumReadBytes, length);
                queueDepthPeak = Math.Max(queueDepthPeak, queued);
                _metrics.ObserveReceiveQueueDepth(queued);
                _metrics.AddAcceptedBlock(length);
            }
            int remainingBytesAvailable = _transport.BytesAvailable;
            bool capacityLimited = remainingBytesAvailable > 0 && _capacitySlots.CurrentCount == 0;
            CompleteDiagnosticRead(
                diagnosticBatchId,
                readBlocks,
                readBytes,
                maximumReadBytes,
                queueDepthPeak,
                remainingBytesAvailable,
                capacityLimited);
        }
    }

    private async Task ProcessAsync()
    {
        try
        {
            await foreach (ReceiveBlock block in _channel.Reader.ReadAllAsync(_processorCancellation.Token).ConfigureAwait(false))
            {
                using (block)
                {
                    try
                    {
                        long startedAt = Stopwatch.GetTimestamp();
                        await _sink.ProcessAsync(block, _processorCancellation.Token).ConfigureAwait(false);
                        CompleteDiagnosticProcessing(
                            block.DiagnosticBatchId,
                            block.DiagnosticFormattedLines,
                            Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _queuedBlocks);
                        _capacitySlots.Release();
                        if (_transport is not IDedicatedReceiveTransport &&
                            Volatile.Read(ref _stopping) == 0 && _transport.BytesAvailable > 0)
                        {
                            ReadAvailableIntoChannel();
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_processorCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            FaultPipeline(exception);
            DrainQueuedBlocks();
        }
    }

    private void DrainQueuedBlocks()
    {
        while (_channel.Reader.TryRead(out ReceiveBlock? block))
        {
            block.Dispose();
            Interlocked.Decrement(ref _queuedBlocks);
            _capacitySlots.Release();
        }
    }
}
