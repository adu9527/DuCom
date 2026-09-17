using System.Collections.Concurrent;
using DuCom.Core.Logging;
using DuCom.Core.Parsing;
using DuCom.Core.Ports;
using DuCom.Core.Sending;
using DuCom.Core.Sessions;

namespace DuCom.Core.Tests.Sessions;

public sealed class RawTrafficTests
{
    [Fact]
    public async Task SubscriptionOwnsBytesAndPreservesDirectionOrderingAndOffsets()
    {
        SessionRawTapHub hub = new();
        await using RawTrafficSubscription subscription = hub.Subscribe(new RawTrafficSubscriptionOptions
        {
            Id = "owned",
            MaximumBlocks = 8,
            MaximumBytes = 64,
        });
        Guid generation = hub.BeginGeneration(settingsRevision: 7);
        byte[] rx = [1, 2, 3];
        byte[] tx = [4, 5];

        hub.PublishReceive(rx, DateTimeOffset.UtcNow);
        hub.PublishTransmit(tx, DateTimeOffset.UtcNow);
        rx[0] = 99;
        tx[0] = 99;

        RawTrafficSnapshot snapshot = await subscription.ReadAsync();
        Assert.Collection(
            snapshot.Records,
            record =>
            {
                Assert.Equal(RawTrafficDirection.Rx, record.Direction);
                Assert.Equal([1, 2, 3], record.Bytes.ToArray());
                Assert.Equal(0, record.ByteOffset);
                Assert.Equal(new RawTrafficCursor(generation, 1, 3, 0), record.Cursor);
                Assert.Equal(7, record.SettingsRevision);
            },
            record =>
            {
                Assert.Equal(RawTrafficDirection.Tx, record.Direction);
                Assert.Equal([4, 5], record.Bytes.ToArray());
                Assert.Equal(0, record.ByteOffset);
                Assert.Equal(new RawTrafficCursor(generation, 2, 3, 2), record.Cursor);
                Assert.Equal(7, record.SettingsRevision);
            });
        Assert.True(snapshot.Records[1].MonotonicTimestamp >= snapshot.Records[0].MonotonicTimestamp);
    }

    [Fact]
    public async Task SubscriptionsOwnIndependentByteArrays()
    {
        SessionRawTapHub hub = new();
        await using RawTrafficSubscription first = hub.Subscribe(new RawTrafficSubscriptionOptions { Id = "first" });
        await using RawTrafficSubscription second = hub.Subscribe(new RawTrafficSubscriptionOptions { Id = "second" });
        hub.BeginGeneration(0);

        hub.PublishReceive(new byte[] { 1, 2 }, DateTimeOffset.UtcNow);

        RawTrafficRecord firstRecord = Assert.Single((await first.ReadAsync()).Records);
        RawTrafficRecord secondRecord = Assert.Single((await second.ReadAsync()).Records);
        Assert.True(System.Runtime.InteropServices.MemoryMarshal.TryGetArray(firstRecord.Bytes, out ArraySegment<byte> firstArray));
        firstArray.Array![firstArray.Offset] = 99;
        Assert.Equal([1, 2], secondRecord.Bytes.ToArray());
    }

    [Fact]
    public async Task OverflowDropsOldestWithExactBlockAndByteCounters()
    {
        SessionRawTapHub hub = new();
        await using RawTrafficSubscription subscription = hub.Subscribe(new RawTrafficSubscriptionOptions
        {
            Id = "bounded",
            MaximumBlocks = 2,
            MaximumBytes = 5,
        });
        hub.BeginGeneration(0);

        hub.PublishReceive(new byte[] { 1, 2 }, DateTimeOffset.UtcNow);
        hub.PublishReceive(new byte[] { 3, 4, 5 }, DateTimeOffset.UtcNow);
        hub.PublishReceive(new byte[] { 6, 7, 8, 9 }, DateTimeOffset.UtcNow);

        RawTrafficSnapshot snapshot = await subscription.ReadAsync();
        RawTrafficRecord record = Assert.Single(snapshot.Records);
        Assert.Equal([6, 7, 8, 9], record.Bytes.ToArray());
        Assert.Equal(2, snapshot.Gap?.DroppedBlocks);
        Assert.Equal(5, snapshot.Gap?.DroppedBytes);
        Assert.Equal(2, snapshot.TotalDroppedBlocks);
        Assert.Equal(5, snapshot.TotalDroppedBytes);
    }

    [Fact]
    public async Task FaultsIncludeSubscriptionIdAndDisposalIsIdempotent()
    {
        SessionRawTapHub hub = new();
        RawTrafficSubscription subscription = hub.Subscribe(new RawTrafficSubscriptionOptions { Id = "consumer" });
        RawTrafficFault? observed = null;
        hub.Faulted += (_, fault) => observed = fault;

        hub.ReportSubscriptionFault("consumer", new InvalidOperationException("consumer failed"));

        RawTrafficSnapshot faultSnapshot = await subscription.ReadAsync();
        Assert.Equal("consumer", faultSnapshot.Fault?.SubscriptionId);
        Assert.Contains("consumer failed", faultSnapshot.Fault?.Message);
        Assert.Equal("consumer", observed?.SubscriptionId);
        subscription.Dispose();
        subscription.Dispose();
        await subscription.DisposeAsync();
        Assert.True(subscription.Snapshot().IsDisposed);
    }

    [Fact]
    public void LegacyTapRemainsRxOnlyAndFaultIsObservable()
    {
        SessionRawTapHub hub = new();
        List<byte[]> received = [];
        RawTrafficFault? fault = null;
        hub.Faulted += (_, value) => fault = value;
        hub.Register(new SessionRawTap
        {
            Id = "legacy",
            Publish = (bytes, _) => received.Add(bytes.ToArray()),
        });

        hub.PublishReceive(new byte[] { 1 }, DateTimeOffset.UtcNow);
        hub.PublishTransmit(new byte[] { 2 }, DateTimeOffset.UtcNow);

        Assert.Equal([[1]], received);

        hub.Register(new SessionRawTap
        {
            Id = "throwing",
            Publish = (_, _) => throw new InvalidOperationException("tap failed"),
        });
        hub.PublishReceive(new byte[] { 3 }, DateTimeOffset.UtcNow);
        Assert.Equal("throwing", fault?.SubscriptionId);
    }

    [Fact]
    public async Task SerialSessionPublishesTxOnlyAfterSuccessfulWriteAndRenewsGenerationOnReopen()
    {
        using TemporaryDirectory directory = new();
        FakeTransport transport = new("RAW1");
        await using SerialSession session = CreateSession(transport, directory.Path);
        await using RawTrafficSubscription subscription = session.RawTaps.Subscribe(new RawTrafficSubscriptionOptions { Id = "serial" });

        Assert.Null(session.RuntimeGeneration);
        Assert.Equal(PortCommandResult.Succeeded, await session.OpenAsync());
        Guid firstGeneration = Assert.IsType<Guid>(session.RuntimeGeneration);
        transport.Receive([0x10, 0x11]);
        await WaitUntilAsync(() => session.Snapshot().Metrics.FormattedLogBlocks == 1);
        await session.SendAsync(SendMode.Hex, "20 21", NewlinePolicy.None);

        RawTrafficSnapshot first = await subscription.ReadAsync();
        Assert.Equal([RawTrafficDirection.Rx, RawTrafficDirection.Tx], first.Records.Select(record => record.Direction));
        Assert.All(first.Records, record => Assert.Equal(firstGeneration, record.Cursor.Generation));
        Assert.Equal(PortCommandResult.Succeeded, await session.CloseAsync());
        Assert.Null(session.RuntimeGeneration);
        Assert.Equal("Closed", (await subscription.ReadAsync()).Gap?.Reason);

        Assert.Equal(PortCommandResult.Succeeded, await session.OpenAsync());
        Guid secondGeneration = Assert.IsType<Guid>(session.RuntimeGeneration);
        Assert.NotEqual(firstGeneration, secondGeneration);
        await session.SendAsync(SendMode.Hex, "30", NewlinePolicy.None);
        RawTrafficRecord reopened = Assert.Single((await subscription.ReadAsync()).Records);
        Assert.Equal(new RawTrafficCursor(secondGeneration, 1, 0, 1), reopened.Cursor);
        Assert.Equal(0, reopened.ByteOffset);
    }

    [Fact]
    public async Task SerialReceivePublishesBeforeFormattingAndCarriesSuccessfulSettingsRevision()
    {
        using TemporaryDirectory directory = new();
        FakeTransport transport = new("RAW-ORDER");
        await using SerialSession session = CreateSession(transport, directory.Path);
        await using RawTrafficSubscription subscription = session.RawTaps.Subscribe(new RawTrafficSubscriptionOptions { Id = "serial" });
        bool publishedBeforeFormatting = false;
        using IDisposable legacy = session.RawTaps.Register(new SessionRawTap
        {
            Id = "ordering",
            Publish = (_, _) => publishedBeforeFormatting = session.Snapshot().Metrics.FormattedLogBlocks == 0,
        });
        await session.OpenAsync();
        await session.ApplySettingsAsync(session.Settings with { BaudRate = 230_400 });

        transport.Receive([0x41]);
        await WaitUntilAsync(() => session.Snapshot().Metrics.FormattedLogBlocks == 1);

        Assert.True(publishedBeforeFormatting);
        Assert.Equal(1, Assert.Single((await subscription.ReadAsync()).Records).SettingsRevision);
    }

    [Fact]
    public async Task FailedSerialWriteDoesNotPublishTx()
    {
        using TemporaryDirectory directory = new();
        FakeTransport transport = new("RAW2") { WriteException = new IOException("write failed") };
        await using SerialSession session = CreateSession(transport, directory.Path);
        await using RawTrafficSubscription subscription = session.RawTaps.Subscribe(new RawTrafficSubscriptionOptions { Id = "serial" });
        await session.OpenAsync();

        await Assert.ThrowsAsync<IOException>(async () => await session.SendAsync(SendMode.Hex, "AA", NewlinePolicy.None));

        Assert.Empty(subscription.Snapshot().Records);
    }

    private static SerialSession CreateSession(FakeTransport transport, string logDirectory) => new(
        transport,
        transport.Settings,
        ReceiveDisplayMode.Str,
        timestampEnabled: false,
        new SessionLogWriterOptions(logDirectory, transport.Settings.PortName),
        lineBudgetBytes: 1024 * 1024);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class FakeTransport(string portName) : ISerialTransport, ISerialSettingsTransport
    {
        private readonly ConcurrentQueue<byte[]> _received = new();

        public event EventHandler? DataAvailable;
        public event EventHandler<TransportDisconnectedEventArgs>? Disconnected { add { } remove { } }
        public SerialPortSettings Settings { get; private set; } = SerialPortSettings.Default(portName);
        public int BytesAvailable => _received.TryPeek(out byte[]? bytes) ? bytes.Length : 0;
        public Exception? WriteException { get; init; }
        public ValueTask OpenAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask CloseAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public int Read(Span<byte> destination)
        {
            Assert.True(_received.TryDequeue(out byte[]? bytes));
            bytes.CopyTo(destination);
            return bytes.Length;
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            if (WriteException is not null)
            {
                throw WriteException;
            }

            return ValueTask.CompletedTask;
        }

        public void ApplySettings(SerialPortSettings settings) => Settings = settings;

        public void Receive(byte[] bytes)
        {
            _received.Enqueue(bytes);
            DataAvailable?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DuCom.RawTrafficTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, true);
            }
        }
    }
}
