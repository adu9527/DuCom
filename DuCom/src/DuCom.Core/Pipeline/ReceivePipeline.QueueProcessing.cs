using DuCom.Core.Parsing;

namespace DuCom.Core.Pipeline;

public sealed partial class ReceivePipeline
{
    private void OnDataAvailable(object? sender, EventArgs e)
    {
        lock (_readGate)
        {
            if (Volatile.Read(ref _stopping) != 0)
            {
                return;
            }

            Interlocked.Increment(ref _activeCallbacks);
            _callbacksIdle.Reset();
        }
        try
        {
            ReceiveFormattingProfile formattingProfile = Volatile.Read(ref _formattingProfile);
            ReadAvailableIntoChannel(formattingProfile);
        }
        catch (Exception exception)
        {
            FaultPipeline(exception);
        }
        finally
        {
            lock (_readGate)
            {
                if (Interlocked.Decrement(ref _activeCallbacks) == 0)
                {
                    _callbacksIdle.Set();
                }
            }
        }
    }

    private void ReadAvailableIntoChannel(ReceiveFormattingProfile? formattingProfile = null)
    {
        lock (_readGate)
        {
            formattingProfile ??= Volatile.Read(ref _formattingProfile);
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
                    formattingProfile);
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
                        await _sink.ProcessAsync(block, _processorCancellation.Token).ConfigureAwait(false);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _queuedBlocks);
                        _capacitySlots.Release();
                        if (Volatile.Read(ref _stopping) == 0 && _transport.BytesAvailable > 0)
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
