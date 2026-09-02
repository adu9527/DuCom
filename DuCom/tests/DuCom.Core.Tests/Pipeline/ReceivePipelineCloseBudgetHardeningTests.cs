using System.Buffers;
using System.Diagnostics;
using DuCom.Core.Diagnostics;
using DuCom.Core.Pipeline;
using DuCom.Core.Ports;
using Xunit;

namespace DuCom.Core.Tests.Pipeline;

/// <summary>
/// 2026-08-28 review round 2: the close budget must cover the capacity slot wait, the
/// transport read, the in-flight callback wait, and the processor drain — one deadline,
/// explicit faults, no hangs.
/// </summary>
public sealed class ReceivePipelineCloseBudgetHardeningTests
{
    [Fact]
    public async Task PermanentlyBlockedSinkFaultsCloseWithinBudget()
    {
        FakeTransport transport = new();
        NeverCompletingSink sink = new();
        LoadMetrics metrics = new();
        await using ReceivePipeline pipeline = new(
            transport, sink, metrics, ArrayPool<byte>.Shared,
            capacity: 1, maximumReadSize: 16,
            drainTimeout: TimeSpan.FromMilliseconds(300));

        await pipeline.StartAsync();
        transport.Enqueue([1]);
        transport.Enqueue([2]);
        transport.RaiseDataAvailable(); // first block enters the sink and blocks there

        Stopwatch clock = Stopwatch.StartNew();
        await pipeline.StopAsync();
        clock.Stop();

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"stop took {clock.Elapsed}");
        Assert.NotNull(pipeline.Fault);
        Assert.Contains("waiting for receive capacity", pipeline.Fault.Message, StringComparison.Ordinal);
        Assert.True(metrics.Snapshot().Faults >= 1);
    }

    [Fact]
    public async Task BlockingTransportReadFaultsCloseWithinBudget()
    {
        BlockingReadTransport transport = new();
        RecordingSink sink = new();
        LoadMetrics metrics = new();
        await using ReceivePipeline pipeline = new(
            transport, sink, metrics, ArrayPool<byte>.Shared,
            capacity: 4, maximumReadSize: 16,
            drainTimeout: TimeSpan.FromMilliseconds(300));

        await pipeline.StartAsync();
        Task callback = Task.Run(transport.RaiseDataAvailable); // callback enters Read and blocks
        await WaitUntilAsync(() => transport.ReadEntries >= 1);

        Stopwatch clock = Stopwatch.StartNew();
        await pipeline.StopAsync();
        clock.Stop();

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"stop took {clock.Elapsed}");
        Assert.NotNull(pipeline.Fault);
        Assert.Contains("in-flight receive callback", pipeline.Fault.Message, StringComparison.Ordinal);
        Assert.True(metrics.Snapshot().Faults >= 1);

        // Let the stuck callback exit before the pipeline disposal releases its objects.
        transport.Release();
        await callback;
    }

    [Fact]
    public async Task BlockingReadInsideDrainFaultsCloseWithinBudget()
    {
        BlockingReadTransport transport = new();
        RecordingSink sink = new();
        LoadMetrics metrics = new();
        await using ReceivePipeline pipeline = new(
            transport, sink, metrics, ArrayPool<byte>.Shared,
            capacity: 4, maximumReadSize: 16,
            drainTimeout: TimeSpan.FromMilliseconds(300));

        await pipeline.StartAsync();
        // Nothing enqueued via events: BytesAvailable > 0 pulls the drain itself into the
        // blocking read.
        transport.MakeAvailable();

        Stopwatch clock = Stopwatch.StartNew();
        await pipeline.StopAsync();
        clock.Stop();

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"stop took {clock.Elapsed}");
        Assert.NotNull(pipeline.Fault);
        Assert.Contains("reading from the transport", pipeline.Fault.Message, StringComparison.Ordinal);
        transport.Release();
    }

    [Fact]
    public async Task StopAsyncEntryDoesNotBlockBehindAnInFlightCallback()
    {
        BlockingReadTransport transport = new();
        RecordingSink sink = new();
        LoadMetrics metrics = new();
        await using ReceivePipeline pipeline = new(
            transport, sink, metrics, ArrayPool<byte>.Shared,
            capacity: 4, maximumReadSize: 16,
            drainTimeout: TimeSpan.FromSeconds(5));

        await pipeline.StartAsync();
        Task callback = Task.Run(transport.RaiseDataAvailable);
        await WaitUntilAsync(() => transport.ReadEntries >= 1);

        // The synchronous entry part of StopAsync must return promptly even while the
        // callback is stuck inside the blocking read.
        Stopwatch clock = Stopwatch.StartNew();
        Task stop = pipeline.StopAsync();
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"StopAsync entry blocked for {clock.Elapsed}");
        Assert.False(stop.IsCompleted);

        transport.Release();
        await callback;
        await stop;
    }

    [Fact]
    public async Task CapacityGreaterThanOneDrainsQueuedBlocksExactlyOnceAfterFirstSinkBlockTimesOut()
    {
        FakeTransport transport = new();
        ManuallyReleasedSink sink = new();
        TrackingPool pool = new();
        LoadMetrics metrics = new();
        ReceivePipeline pipeline = new(
            transport, sink, metrics, pool,
            capacity: 4, maximumReadSize: 16,
            drainTimeout: TimeSpan.FromMilliseconds(200));

        await pipeline.StartAsync();
        transport.Enqueue([1]);
        transport.Enqueue([2]);
        transport.Enqueue([3]);
        transport.RaiseDataAvailable();
        await sink.Entered;

        await pipeline.StopAsync();
        await pipeline.DisposeAsync();

        Assert.Equal(2, pool.ReturnCount);
        sink.Release();
        await WaitUntilAsync(() => pool.ReturnCount == 3);

        Assert.Equal(3, pool.RentCount);
        Assert.Equal(3, pool.ReturnCount);
        Assert.NotNull(pipeline.Fault);
        Assert.Contains("processor", pipeline.Fault.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallbackMayReturnAfterStopTimeoutAndDisposeWithoutTouchingDisposedSynchronizationObjects()
    {
        BlockingReadTransport transport = new();
        TrackingPool pool = new();
        LoadMetrics metrics = new();
        ReceivePipeline pipeline = new(
            transport, new RecordingSink(), metrics, pool,
            capacity: 4, maximumReadSize: 16,
            drainTimeout: TimeSpan.FromMilliseconds(200));

        await pipeline.StartAsync();
        Task callback = Task.Run(transport.RaiseDataAvailable);
        await WaitUntilAsync(() => transport.ReadEntries >= 1);

        await pipeline.StopAsync();
        await pipeline.DisposeAsync();
        transport.Release();
        await callback;
        await WaitUntilAsync(() => pool.ReturnCount == pool.RentCount);

        Assert.Equal(pool.RentCount, pool.ReturnCount);
        Assert.NotNull(pipeline.Fault);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class RecordingSink : IReceiveBlockSink
    {
        public List<string> Payloads { get; } = [];

        public ValueTask ProcessAsync(ReceiveBlock block, CancellationToken cancellationToken)
        {
            Payloads.Add(Convert.ToHexString(block.Memory.Span));
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>A sink whose first block never completes: the capacity slot is never released.</summary>
    private sealed class NeverCompletingSink : IReceiveBlockSink
    {
        public ValueTask ProcessAsync(ReceiveBlock block, CancellationToken cancellationToken) =>
            new(Task.Delay(Timeout.Infinite, cancellationToken));
    }

    private sealed class ManuallyReleasedSink : IReceiveBlockSink
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;

        public ValueTask ProcessAsync(ReceiveBlock block, CancellationToken cancellationToken)
        {
            _entered.TrySetResult();
            return new ValueTask(_release.Task);
        }

        public void Release() => _release.TrySetResult();
    }

    private sealed class TrackingPool : ArrayPool<byte>
    {
        private int _rentCount;
        private int _returnCount;

        public int RentCount => Volatile.Read(ref _rentCount);

        public int ReturnCount => Volatile.Read(ref _returnCount);

        public override byte[] Rent(int minimumLength)
        {
            Interlocked.Increment(ref _rentCount);
            return new byte[minimumLength];
        }

        public override void Return(byte[] array, bool clearArray = false) => Interlocked.Increment(ref _returnCount);
    }

    private sealed class FakeTransport : IReceiveTransport
    {
        private readonly Queue<byte[]> _payloads = new();
        private readonly object _gate = new();

        public event EventHandler? DataAvailable;

        public int BytesAvailable
        {
            get
            {
                lock (_gate)
                {
                    return _payloads.TryPeek(out byte[]? payload) ? payload.Length : 0;
                }
            }
        }

        public int Read(Span<byte> destination)
        {
            lock (_gate)
            {
                byte[] payload = _payloads.Dequeue();
                payload.CopyTo(destination);
                return payload.Length;
            }
        }

        public void Enqueue(byte[] payload)
        {
            lock (_gate)
            {
                _payloads.Enqueue(payload);
            }
        }

        public void RaiseDataAvailable() => DataAvailable?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Read blocks until released; the first released read consumes the availability.</summary>
    private sealed class BlockingReadTransport : IReceiveTransport, IDisposable
    {
        private readonly ManualResetEventSlim _readGate = new(false);
        private int _available;

        public event EventHandler? DataAvailable;

        public int ReadEntries { get; private set; }

        public int BytesAvailable => Volatile.Read(ref _available);

        public int Read(Span<byte> destination)
        {
            ReadEntries++;
            _readGate.Wait();
            Volatile.Write(ref _available, 0);
            destination[0] = 1;
            return 1;
        }

        public void MakeAvailable() => Volatile.Write(ref _available, 16);

        public void Release() => _readGate.Set();

        public void RaiseDataAvailable()
        {
            MakeAvailable();
            DataAvailable?.Invoke(this, EventArgs.Empty);
        }

        public void Dispose() => _readGate.Dispose();
    }
}
