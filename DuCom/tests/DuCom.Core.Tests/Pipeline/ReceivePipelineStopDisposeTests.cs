using System.Buffers;
using DuCom.Core.Diagnostics;
using DuCom.Core.Pipeline;
using DuCom.Core.Ports;
using Xunit;

namespace DuCom.Core.Tests.Pipeline;

/// <summary>
/// ADR-0004 follow-up: concurrent Stop/Dispose semantics, shared stop task, and the
/// sustained-input drain budget for <see cref="ReceivePipeline"/>.
/// </summary>
public sealed class ReceivePipelineStopDisposeTests
{
    [Fact]
    public async Task ConcurrentStopAsyncCallsAllAwaitTheSameSingleStop()
    {
        TrackingPool pool = new();
        SlowCallbackTransport transport = new();
        RecordingSink sink = new();
        LoadMetrics metrics = new();
        await using ReceivePipeline pipeline = new(transport, sink, metrics, pool, capacity: 8, maximumReadSize: 32);
        await pipeline.StartAsync();

        transport.Enqueue([1, 2, 3]);
        Task callback = Task.Run(transport.RaiseDataAvailable);
        await WaitUntilAsync(() => transport.ReadEntries >= 1);
        // StopAsync is issued on pool threads: its synchronous quiesce step blocks until
        // the in-flight callback exits, which the test controls below. The callback owns
        // the read gate, so no stop can complete before ReleaseCallbacks.
        Task[] stops = [.. Enumerable.Range(0, 5).Select(_ => Task.Run(pipeline.StopAsync))];
        transport.ReleaseCallbacks();
        await callback;
        await Task.WhenAll(stops);

        Assert.Equal(["010203"], sink.Payloads);
        Assert.Equal(pool.RentCount, pool.ReturnCount);
        Assert.Equal(1, transport.CallbackExecutions);
    }

    [Fact]
    public async Task ConcurrentStopAndDisposeAreSafeAndReturnAllBuffers()
    {
        TrackingPool pool = new();
        FakeReceiveTransport transport = new();
        RecordingSink sink = new(delay: TimeSpan.FromMilliseconds(2));
        LoadMetrics metrics = new();
        ReceivePipeline pipeline = new(transport, sink, metrics, pool, capacity: 4, maximumReadSize: 16);
        await pipeline.StartAsync();

        for (int index = 0; index < 32; index++)
        {
            transport.Enqueue([(byte)index]);
        }

        transport.RaiseDataAvailable();
        Task stop = pipeline.StopAsync();
        ValueTask dispose = pipeline.DisposeAsync();
        await stop;
        await dispose;

        Assert.Equal(32, sink.Payloads.Count);
        Assert.Equal(pool.RentCount, pool.ReturnCount);
    }

    [Fact]
    public async Task ConcurrentDisposeCallsShareOneDisposalAndDoNotThrow()
    {
        TrackingPool pool = new();
        FakeReceiveTransport transport = new();
        RecordingSink sink = new(delay: TimeSpan.FromMilliseconds(2));
        LoadMetrics metrics = new();
        ReceivePipeline pipeline = new(transport, sink, metrics, pool, capacity: 4, maximumReadSize: 16);
        await pipeline.StartAsync();

        for (int index = 0; index < 24; index++)
        {
            transport.Enqueue([(byte)index]);
        }

        transport.RaiseDataAvailable();
        ValueTask first = pipeline.DisposeAsync();
        ValueTask second = pipeline.DisposeAsync();
        await first;
        await second;

        Assert.Equal(24, sink.Payloads.Count);
        Assert.Equal(pool.RentCount, pool.ReturnCount);
    }

    [Fact]
    public async Task FullChannelWithSlowSinkStillCompletesStopAndDispose()
    {
        TrackingPool pool = new();
        FakeReceiveTransport transport = new();
        RecordingSink sink = new(delay: TimeSpan.FromMilliseconds(5));
        LoadMetrics metrics = new();
        ReceivePipeline pipeline = new(transport, sink, metrics, pool, capacity: 2, maximumReadSize: 8);
        await pipeline.StartAsync();

        for (int index = 0; index < 50; index++)
        {
            transport.Enqueue([(byte)index]);
        }

        transport.RaiseDataAvailable();
        await pipeline.DisposeAsync();

        Assert.Equal(50, sink.Payloads.Count);
        Assert.Equal(pool.RentCount, pool.ReturnCount);
        PipelineMetricsSnapshot snapshot = metrics.Snapshot();
        Assert.Equal(50, snapshot.AcceptedBlocks);
        Assert.Equal(0, snapshot.Faults);
    }

    [Fact]
    public async Task StopWaitsForInFlightCallbackBeforeDraining()
    {
        TrackingPool pool = new();
        SlowCallbackTransport transport = new();
        RecordingSink sink = new();
        LoadMetrics metrics = new();
        await using ReceivePipeline pipeline = new(transport, sink, metrics, pool, capacity: 8, maximumReadSize: 32);
        await pipeline.StartAsync();

        transport.Enqueue([1]);
        transport.Enqueue([2]);
        Task callback = Task.Run(transport.RaiseDataAvailable);
        await WaitUntilAsync(() => transport.ReadEntries >= 1);
        Task stop = Task.Run(pipeline.StopAsync);
        // The callback owns the read gate and blocks in transport.Read, so the stop task
        // cannot possibly complete before ReleaseCallbacks runs.
        Assert.False(stop.IsCompleted);
        transport.ReleaseCallbacks();
        await callback;
        await stop;

        Assert.Equal(["01", "02"], sink.Payloads);
        Assert.Equal(pool.RentCount, pool.ReturnCount);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    [Fact]
    public async Task ContinuousInputExceedingByteBudgetFaultsExplicitlyInsteadOfLoopingForever()
    {
        TrackingPool pool = new();
        ContinuousTransport transport = new(chunkSize: 64);
        RecordingSink sink = new();
        LoadMetrics metrics = new();
        await using ReceivePipeline pipeline = new(
            transport,
            sink,
            metrics,
            pool,
            capacity: 8,
            maximumReadSize: 64,
            drainTimeout: TimeSpan.FromSeconds(10),
            maximumDrainBytes: 256);

        await pipeline.StartAsync();
        await pipeline.StopAsync();

        Assert.NotNull(pipeline.Fault);
        Assert.Contains("drain exceeded", pipeline.Fault.Message, StringComparison.Ordinal);
        Assert.Contains("byte budget", pipeline.Fault.Message, StringComparison.Ordinal);
        Assert.True(metrics.Snapshot().Faults >= 1);
        Assert.True(sink.Payloads.Count > 0);
        Assert.Equal(pool.RentCount, pool.ReturnCount);
    }

    [Fact]
    public async Task ContinuousInputExceedingTimeBudgetFaultsExplicitly()
    {
        TrackingPool pool = new();
        ContinuousTransport transport = new(chunkSize: 32);
        RecordingSink sink = new();
        LoadMetrics metrics = new();
        await using ReceivePipeline pipeline = new(
            transport,
            sink,
            metrics,
            pool,
            capacity: 8,
            maximumReadSize: 32,
            drainTimeout: TimeSpan.FromMilliseconds(100),
            maximumDrainBytes: 64L * 1024 * 1024);

        await pipeline.StartAsync();
        await pipeline.StopAsync();

        Assert.NotNull(pipeline.Fault);
        Assert.Contains("time budget", pipeline.Fault.Message, StringComparison.Ordinal);
        Assert.True(metrics.Snapshot().Faults >= 1);
        Assert.Equal(pool.RentCount, pool.ReturnCount);
    }

    [Fact]
    public async Task FiniteBacklogUnderBudgetDrainsCompletelyWithoutFault()
    {
        TrackingPool pool = new();
        FakeReceiveTransport transport = new();
        RecordingSink sink = new();
        LoadMetrics metrics = new();
        await using ReceivePipeline pipeline = new(
            transport,
            sink,
            metrics,
            pool,
            capacity: 8,
            maximumReadSize: 32,
            drainTimeout: TimeSpan.FromSeconds(5),
            maximumDrainBytes: 4_096);

        await pipeline.StartAsync();
        for (int index = 0; index < 100; index++)
        {
            transport.Enqueue([(byte)index]);
        }

        await pipeline.StopAsync();

        Assert.Null(pipeline.Fault);
        Assert.Equal(100, sink.Payloads.Count);
        Assert.Equal(0, metrics.Snapshot().Faults);
        Assert.Equal(pool.RentCount, pool.ReturnCount);
    }

    private sealed class RecordingSink(TimeSpan delay = default) : IReceiveBlockSink
    {
        public List<string> Payloads { get; } = [];

        public async ValueTask ProcessAsync(ReceiveBlock block, CancellationToken cancellationToken)
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken);
            }

            lock (Payloads)
            {
                Payloads.Add(Convert.ToHexString(block.Memory.Span));
            }
        }
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

    private sealed class FakeReceiveTransport : IReceiveTransport
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

    /// <summary>
    /// A transport whose Read blocks until the test releases it, proving Stop waits for an
    /// in-flight DataAvailable callback before draining.
    /// </summary>
    private sealed class SlowCallbackTransport : IReceiveTransport, IDisposable
    {
        private readonly Queue<byte[]> _payloads = new();
        private readonly ManualResetEventSlim _readGate = new(false);
        private readonly object _gate = new();
        private int _readEntries;
        private int _callbackExecutions;

        public event EventHandler? DataAvailable;

        public int CallbackExecutions => Volatile.Read(ref _callbackExecutions);

        public int ReadEntries => Volatile.Read(ref _readEntries);

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
            Interlocked.Increment(ref _readEntries);
            _readGate.Wait();
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

        public void ReleaseCallbacks() => _readGate.Set();

        public void RaiseDataAvailable()
        {
            Interlocked.Increment(ref _callbackExecutions);
            DataAvailable?.Invoke(this, EventArgs.Empty);
        }

        public void Dispose() => _readGate.Dispose();
    }

    /// <summary>Simulates a device that keeps producing bytes faster than they can drain.</summary>
    private sealed class ContinuousTransport(int chunkSize) : IReceiveTransport
    {
        public event EventHandler? DataAvailable
        {
            add { }
            remove { }
        }

        public int BytesAvailable => chunkSize;

        public int Read(Span<byte> destination)
        {
            for (int index = 0; index < chunkSize; index++)
            {
                destination[index] = (byte)index;
            }

            return chunkSize;
        }
    }
}
