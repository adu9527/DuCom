using System.Diagnostics;

namespace DuCom.Core.Pipeline;

public sealed partial class ReceivePipeline
{
    /// <summary>
    /// Stops accepting new external DataAvailable events first (inside <see cref="StopAsync"/>,
    /// before this method runs), then reads every already-arrived transport byte into the
    /// receive Channel with backpressure-respecting awaits. A close under sustained input can
    /// never finish draining a transport that keeps producing, so the drain is bounded by a
    /// wall-clock budget and a maximum appended-byte budget. Exceeding either budget faults
    /// the pipeline with an explicit reason (surfaced as a session fault; never a silent
    /// success) and lets the caller proceed to a forced transport close. The receive-capacity
    /// wait uses the same deadline token, so a sink that never releases slots faults the
    /// close on time instead of hanging it.
    /// </summary>
    private async Task DrainTransportBufferAsync(long deadlineTicks, CancellationToken budget)
    {
        long drainedBytes = 0;
        while (_transport.BytesAvailable > 0)
        {
            bool outOfTime = Stopwatch.GetTimestamp() >= deadlineTicks;
            if (drainedBytes >= _maximumDrainBytes || outOfTime)
            {
                string budgetName = outOfTime ? $"time budget {_drainTimeout.TotalSeconds:0.###}s" : $"byte budget {_maximumDrainBytes} bytes";
                FaultPipeline(NewDrainBudgetExceeded(budgetName, drainedBytes));
                return;
            }

            int requested = Math.Min(_transport.BytesAvailable, _maximumReadSize);
            byte[] buffer = _bufferPool.Rent(requested);
            Task<int> read = Task.Run(() => _transport.Read(buffer.AsSpan(0, requested)));
            int length;
            try
            {
                // The read itself is deadline-bounded: a driver-like read that never
                // returns faults the close on time instead of hanging it. The pool thread
                // may stay blocked until the forced transport close unblocks it.
                length = await read.WaitAsync(budget).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (budget.IsCancellationRequested)
            {
                ReturnWhenReadCompletes(read, buffer);
                FaultPipeline(new InvalidOperationException(
                    $"Receive close time budget {_drainTimeout.TotalSeconds:0.###}s was exceeded while reading from the transport after draining {drainedBytes} bytes " +
                    $"with {_transport.BytesAvailable} bytes still buffered. The transport will be force-closed; this close is reported as a session fault instead of a silent loss."));
                return;
            }
            catch (Exception exception)
            {
                _bufferPool.Return(buffer);
                FaultPipeline(exception);
                return;
            }

            if (length <= 0)
            {
                _bufferPool.Return(buffer);
                break;
            }

            try
            {
                await _capacitySlots.WaitAsync(budget).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (budget.IsCancellationRequested)
            {
                _bufferPool.Return(buffer);
                FaultPipeline(new InvalidOperationException(
                    $"Receive close time budget {_drainTimeout.TotalSeconds:0.###}s was exceeded while waiting for receive capacity after draining {drainedBytes} bytes " +
                    $"with {_transport.BytesAvailable} bytes still buffered (the receive sink is not releasing capacity). " +
                    "The transport will be force-closed; the remaining buffered bytes cannot be logged and this close is reported as a session fault instead of a silent loss."));
                return;
            }

            _metrics.AddProducedBlock(length);
            ReceiveBlock block = new(
                _bufferPool,
                buffer,
                length,
                DateTimeOffset.UtcNow,
                Volatile.Read(ref _formattingProfile));
            try
            {
                await _channel.Writer.WriteAsync(block, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // The Channel is already closed (faulted or completed). Reject the data
                // explicitly like a post-fault callback would and surface the pipeline fault;
                // buffered ownership rules stay intact.
                block.Dispose();
                _capacitySlots.Release();
                if (Fault is not null)
                {
                    _metrics.AddFault();
                }

                return;
            }

            drainedBytes += length;
            int queued = Interlocked.Increment(ref _queuedBlocks);
            _metrics.ObserveReceiveQueueDepth(queued);
            _metrics.AddAcceptedBlock(length);
        }
    }

    private InvalidOperationException NewDrainBudgetExceeded(string budgetName, long drainedBytes) => new(
        $"Receive drain exceeded its {budgetName} after draining {drainedBytes} bytes with {_transport.BytesAvailable} bytes still buffered. " +
        "The transport will be force-closed; the remaining buffered bytes cannot be logged and this close is reported as a session fault instead of a silent loss.");

    /// <summary>
    /// A deadline-abandoned read still owns its rented buffer; the moment the stuck read
    /// finally completes (typically when the forced transport close unblocks it) the buffer
    /// is returned to the pool exactly once.
    /// </summary>
    private void ReturnWhenReadCompletes(Task<int> read, byte[] buffer) =>
        _ = read.ContinueWith(
            completed =>
            {
                try
                {
                    _ = completed.Result;
                }
                catch (Exception)
                {
                    // The read failed; the buffer content is irrelevant.
                }

                _bufferPool.Return(buffer);
            },
            TaskScheduler.Default);
}
