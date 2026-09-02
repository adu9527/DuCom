using System.Buffers;
using DuCom.Core.Diagnostics;
using DuCom.Core.Pipeline;
using DuCom.Core.Ports;

namespace DuCom.Core.Tests.Pipeline;

public sealed class ReceivePipelineTests
{
    [Fact]
    public async Task AcceptedBlocksReachSinkAndBuffersReturnExactlyOnce()
    {
        TrackingPool pool = new();
        FakeReceiveTransport transport = new();
        RecordingSink sink = new();
        LoadMetrics metrics = new();
        await using ReceivePipeline pipeline = new(transport, sink, metrics, pool, capacity: 8, maximumReadSize: 32);
        await pipeline.StartAsync();

        transport.Enqueue([1, 2, 3]);
        transport.Enqueue([4, 5]);
        transport.RaiseDataAvailable();
        await pipeline.StopAsync();

        Assert.Equal(["010203", "0405"], sink.Payloads);
        Assert.Equal(2, pool.RentCount);
        Assert.Equal(2, pool.ReturnCount);
        Assert.Equal(2, metrics.Snapshot().AcceptedBlocks);
    }

    [Fact]
    public async Task SinkFailureReturnsBufferAndFaultsPipeline()
    {
        TrackingPool pool = new();
        FakeReceiveTransport transport = new();
        LoadMetrics metrics = new();
        await using ReceivePipeline pipeline = new(transport, new ThrowingSink(), metrics, pool, 4, 32);
        await pipeline.StartAsync();

        transport.Enqueue([1]);
        transport.RaiseDataAvailable();
        await pipeline.StopAsync();

        Assert.Equal(1, pool.ReturnCount);
        Assert.Equal(1, metrics.Snapshot().Faults);
        Assert.NotNull(pipeline.Fault);
    }

    [Fact]
    public async Task InputAfterSinkFailureIsNotAcceptedAndAllBuffersReturn()
    {
        TrackingPool pool = new();
        FakeReceiveTransport transport = new();
        LoadMetrics metrics = new();
        await using ReceivePipeline pipeline = new(transport, new ThrowingSink(), metrics, pool, 4, 32);
        await pipeline.StartAsync();

        transport.Enqueue([1]);
        transport.RaiseDataAvailable();
        await WaitUntilAsync(() => pipeline.Fault is not null);
        long acceptedAfterFault = metrics.Snapshot().AcceptedBlocks;
        transport.Enqueue([2]);
        transport.RaiseDataAvailable();
        await pipeline.StopAsync();

        Assert.Equal(acceptedAfterFault, metrics.Snapshot().AcceptedBlocks);
        Assert.Equal(pool.RentCount, pool.ReturnCount);
    }

    [Fact]
    public async Task StopDrainsAllAcceptedBlocks()
    {
        FakeReceiveTransport transport = new();
        RecordingSink sink = new(delay: TimeSpan.FromMilliseconds(2));
        LoadMetrics metrics = new();
        await using ReceivePipeline pipeline = new(transport, sink, metrics, ArrayPool<byte>.Shared, 64, 32);
        await pipeline.StartAsync();

        for (int index = 0; index < 20; index++)
        {
            transport.Enqueue([(byte)index]);
        }

        transport.RaiseDataAvailable();
        await pipeline.StopAsync();

        Assert.Equal(20, sink.Payloads.Count);
        Assert.Equal(ShutdownDrainState.NotStarted, metrics.Snapshot().ShutdownDrainState);
    }

    [Fact]
    public async Task StopDrainsTransportQueueThatNeverFiredDataAvailable()
    {
        // Data sits inside the transport queue without any DataAvailable event having been
        // raised. A close must still read every already-arrived byte instead of stranding it.
        FakeReceiveTransport transport = new();
        RecordingSink sink = new();
        LoadMetrics metrics = new();
        await using ReceivePipeline pipeline = new(transport, sink, metrics, ArrayPool<byte>.Shared, 8, 32);
        await pipeline.StartAsync();

        for (int index = 0; index < 10; index++)
        {
            transport.Enqueue([(byte)(index + 1)]);
        }

        await pipeline.StopAsync();

        Assert.Equal(10, sink.Payloads.Count);
        PipelineMetricsSnapshot snapshot = metrics.Snapshot();
        Assert.Equal(10, snapshot.ProducedBlocks);
        Assert.Equal(10, snapshot.ProducedBytes); // one single-byte payload per enqueue
        Assert.Equal(snapshot.ProducedBlocks, snapshot.AcceptedBlocks);
    }

    [Fact]
    public async Task StopWhenChannelReachedCapacityStillConsumesEntireTransportBacklog()
    {
        FakeReceiveTransport transport = new();
        RecordingSink sink = new(delay: TimeSpan.FromMilliseconds(1));
        LoadMetrics metrics = new();
        await using ReceivePipeline pipeline = new(transport, sink, metrics, ArrayPool<byte>.Shared, capacity: 4, maximumReadSize: 16);
        await pipeline.StartAsync();

        for (int index = 0; index < 40; index++)
        {
            transport.Enqueue([(byte)index]);
        }

        transport.RaiseDataAvailable(); // capacity fills; reader exits with backlog remaining
        await pipeline.StopAsync();

        Assert.Equal(40, sink.Payloads.Count);
        Assert.Equal(40, metrics.Snapshot().ProducedBytes);
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

            Payloads.Add(Convert.ToHexString(block.Memory.Span));
        }
    }

    private sealed class ThrowingSink : IReceiveBlockSink
    {
        public ValueTask ProcessAsync(ReceiveBlock block, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("sink failed");
    }

    private sealed class TrackingPool : ArrayPool<byte>
    {
        public int RentCount { get; private set; }

        public int ReturnCount { get; private set; }

        public override byte[] Rent(int minimumLength)
        {
            RentCount++;
            return new byte[minimumLength];
        }

        public override void Return(byte[] array, bool clearArray = false) => ReturnCount++;
    }

    private sealed class FakeReceiveTransport : IReceiveTransport
    {
        private readonly Queue<byte[]> _payloads = new();

        public event EventHandler? DataAvailable;

        public int BytesAvailable => _payloads.TryPeek(out byte[]? payload) ? payload.Length : 0;

        public int Read(Span<byte> destination)
        {
            byte[] payload = _payloads.Dequeue();
            payload.CopyTo(destination);
            return payload.Length;
        }

        public void Enqueue(byte[] payload) => _payloads.Enqueue(payload);

        public void RaiseDataAvailable() => DataAvailable?.Invoke(this, EventArgs.Empty);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }
}
