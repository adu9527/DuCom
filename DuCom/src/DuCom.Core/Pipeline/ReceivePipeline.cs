using System.Buffers;
using System.Diagnostics;
using System.Threading.Channels;
using DuCom.Core.Diagnostics;
using DuCom.Core.Parsing;
using DuCom.Core.Ports;

namespace DuCom.Core.Pipeline;

public sealed class ReceivePipeline : IAsyncDisposable
{
    /// <summary>Default wall-clock budget for draining the transport buffer during StopAsync.</summary>
    public static readonly TimeSpan DefaultDrainTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Default maximum number of bytes StopAsync may drain from the transport buffer.</summary>
    public const long DefaultMaximumDrainBytes = 32L * 1024 * 1024;

    private readonly ArrayPool<byte> _bufferPool;
    private readonly Channel<ReceiveBlock> _channel;
    private readonly IReceiveBlockSink _sink;
    private readonly LoadMetrics _metrics;
    private readonly int _maximumReadSize;
    private readonly IReceiveTransport _transport;
    private readonly TimeSpan _drainTimeout;
    private readonly long _maximumDrainBytes;
    private readonly CancellationTokenSource _processorCancellation = new();
    private readonly ManualResetEventSlim _callbacksIdle = new(initialState: true);
    private readonly SemaphoreSlim _capacitySlots;
    private readonly object _readGate = new();
    private readonly object _lifetimeGate = new();
    private ReceiveFormattingProfile _formattingProfile;
    private Task? _processorTask;
    private Task? _stopTask;
    private Task? _disposeTask;
    private Task? _cleanupTask;
    private int _activeCallbacks;
    private int _queuedBlocks;
    private int _started;
    private int _stopping;

    public ReceivePipeline(
        IReceiveTransport transport,
        IReceiveBlockSink sink,
        LoadMetrics metrics,
        ArrayPool<byte> bufferPool,
        int capacity,
        int maximumReadSize,
        TimeSpan? drainTimeout = null,
        long? maximumDrainBytes = null)
        : this(
            transport,
            sink,
            metrics,
            bufferPool,
            capacity,
            maximumReadSize,
            new ReceiveFormattingProfile(0, System.Text.Encoding.UTF8.WebName, ReceiveDisplayMode.Str, false),
            drainTimeout,
            maximumDrainBytes)
    {
    }

    public ReceivePipeline(
        IReceiveTransport transport,
        IReceiveBlockSink sink,
        LoadMetrics metrics,
        ArrayPool<byte> bufferPool,
        int capacity,
        int maximumReadSize,
        ReceiveFormattingProfile formattingProfile,
        TimeSpan? drainTimeout = null,
        long? maximumDrainBytes = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _bufferPool = bufferPool ?? throw new ArgumentNullException(nameof(bufferPool));
        formattingProfile.Validate();
        _formattingProfile = formattingProfile;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumReadSize);
        if (drainTimeout is { } timeout)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        }

        if (maximumDrainBytes is { } budget)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(budget, 0);
        }

        _maximumReadSize = maximumReadSize;
        _drainTimeout = drainTimeout ?? DefaultDrainTimeout;
        _maximumDrainBytes = maximumDrainBytes ?? DefaultMaximumDrainBytes;
        _capacitySlots = new SemaphoreSlim(capacity, capacity);
        _channel = Channel.CreateBounded<ReceiveBlock>(new BoundedChannelOptions(capacity)
        {
            SingleReader = false,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false,
        });
    }

    public Exception? Fault { get; private set; }

    public event EventHandler<Exception>? Faulted;

    public void UpdateFormattingProfile(ReceiveFormattingProfile formattingProfile)
    {
        ArgumentNullException.ThrowIfNull(formattingProfile);
        formattingProfile.Validate();
        ReceiveFormattingProfile current = Volatile.Read(ref _formattingProfile);
        if (formattingProfile.Version <= current.Version)
        {
            throw new ArgumentOutOfRangeException(nameof(formattingProfile), "Formatting profile versions must increase.");
        }

        Volatile.Write(ref _formattingProfile, formattingProfile);
    }

    public Task StartAsync()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return Task.CompletedTask;
        }

        _transport.DataAvailable += OnDataAvailable;
        _processorTask = Task.Run(ProcessAsync);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops the pipeline exactly once. Concurrent callers all await the same shared stop
    /// task; <see cref="DisposeAsync"/> participates in the same task before releasing any
    /// synchronization object. The synchronous entry part never blocks: quiescing is a
    /// lock-free flag flip plus event unsubscribe, so calling from the UI thread is safe.
    /// The whole stop sequence — waiting for in-flight callbacks, draining the transport
    /// buffer (including receive-capacity waits), and draining the processor — shares one
    /// wall-clock budget plus the appended-byte budget; every phase that blows the budget
    /// faults the pipeline with an explicit reason instead of hanging the close.
    /// </summary>
    public Task StopAsync()
    {
        Task? existing = Volatile.Read(ref _stopTask);
        if (existing is not null)
        {
            return existing;
        }

        lock (_lifetimeGate)
        {
            return EnsureStoppedLocked();
        }
    }

    /// <summary>Requires the <see cref="_lifetimeGate"/> lock to be held by the caller.</summary>
    private Task EnsureStoppedLocked()
    {
        if (_stopTask is not null)
        {
            return _stopTask;
        }

        StopAccepting();
        Task stopTask = StopSequenceAsync();
        Volatile.Write(ref _stopTask, stopTask);
        return stopTask;
    }

    private async Task StopSequenceAsync()
    {
        Stopwatch drain = Stopwatch.StartNew();
        long deadlineTicks = Stopwatch.GetTimestamp() + (long)(_drainTimeout.TotalSeconds * Stopwatch.Frequency);
        using CancellationTokenSource budgetCancellation = new(_drainTimeout);
        CancellationToken budget = budgetCancellation.Token;

        // Phase 1: wait for in-flight receive callbacks to exit (bounded by the budget).
        if (Volatile.Read(ref _activeCallbacks) != 0)
        {
            Task waitForIdle = Task.Run(_callbacksIdle.Wait);
            try
            {
                await waitForIdle.WaitAsync(budget).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (budget.IsCancellationRequested)
            {
                FaultPipeline(new InvalidOperationException(
                    $"Receive close time budget {_drainTimeout.TotalSeconds:0.###}s was exceeded while waiting for an in-flight receive callback to exit. " +
                    "The transport will be force-closed; data still held by that callback cannot be logged and this close is reported as a session fault instead of a silent loss."));
            }
        }

        // Phase 2: drain the transport buffer (bounded by the same budget; the capacity
        // slot wait honors the deadline as well so a permanently blocked sink cannot hang
        // the close).
        await DrainTransportBufferAsync(deadlineTicks, budget).ConfigureAwait(false);

        // Phase 3: complete the Channel writer and drain the processor (same budget).
        _channel.Writer.TryComplete(Fault);
        if (_processorTask is not null)
        {
            try
            {
                await _processorTask.WaitAsync(budget).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (budget.IsCancellationRequested)
            {
                FaultPipeline(new InvalidOperationException(
                    $"Receive close time budget {_drainTimeout.TotalSeconds:0.###}s was exceeded while draining the per-port processor (the sink is not completing). " +
                    "Unprocessed receive blocks cannot be logged; this close is reported as a session fault instead of a silent loss. Cancellation has been requested for the processor as a best-effort interrupt."));
                _processorCancellation.Cancel();
                DrainQueuedBlocks();
            }
        }

        drain.Stop();
    }

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

    /// <summary>
    /// Idempotent and concurrency-safe disposal. Concurrent callers share one disposal task.
    /// A bounded stop may return while an uncooperative callback or sink is still running;
    /// in that case final resource release is deferred until those operations really exit.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        lock (_lifetimeGate)
        {
            if (_disposeTask is null)
            {
                // Start the stop sequence under the same lock as StopAsync so the quiesce
                // guarantee (unsubscribe before returning) holds for disposal too.
                Task stopTask = EnsureStoppedLocked();
                _disposeTask = DisposeAfterStopAsync(stopTask);
            }

            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeAfterStopAsync(Task stopTask)
    {
        try
        {
            await stopTask.ConfigureAwait(false);
        }
        catch
        {
            // A failed stop must not prevent releasing the pipeline's own resources; the
            // fault is already recorded on Fault/metrics and surfaced by the session.
        }

        _processorCancellation.Cancel();
        DrainQueuedBlocks();

        lock (_lifetimeGate)
        {
            _cleanupTask ??= CleanupWhenQuiescentAsync();
        }
    }

    private async Task CleanupWhenQuiescentAsync()
    {
        await Task.Run(_callbacksIdle.Wait).ConfigureAwait(false);
        if (_processorTask is not null)
        {
            try
            {
                await _processorTask.ConfigureAwait(false);
            }
            catch
            {
                // Fault is already recorded by the processor path.
            }
        }

        DrainQueuedBlocks();
        _processorCancellation.Dispose();
        _callbacksIdle.Dispose();
        _capacitySlots.Dispose();
    }

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

    private void FaultPipeline(Exception exception)
    {
        StopAccepting();

        if (Fault is null)
        {
            Fault = exception;
            _metrics.AddFault();
            Faulted?.Invoke(this, exception);
        }

        _channel.Writer.TryComplete(exception);
    }

    /// <summary>
    /// Lock-free quiesce: flip the stopping flag and unsubscribe the event. Both are
    /// non-blocking, so the synchronous entry of <see cref="StopAsync"/> can never stall
    /// behind an in-flight callback. A callback that already passed the stopping check is
    /// accounted for by <c>_activeCallbacks</c> and awaited (within the budget) by the stop
    /// sequence; <see cref="ReadAvailableIntoChannel"/> re-checks the flag under the read
    /// gate before every read.
    /// </summary>
    private void StopAccepting()
    {
        Interlocked.Exchange(ref _stopping, 1);
        _transport.DataAvailable -= OnDataAvailable;
    }
}
