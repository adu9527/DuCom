using System.Buffers;
using System.Text;
using DuCom.Core.Diagnostics;
using DuCom.Core.Logging;
using DuCom.Core.Parsing;
using DuCom.Core.Pipeline;
using DuCom.Core.Ports;
using DuCom.Core.Sessions;
using DuCom.Core.Storage;

namespace DuCom.Core.Tests.Pipeline;

public sealed class ReceiveFormattingProfileTests
{
    [Fact]
    public async Task QueuedBlockKeepsProfileCapturedWhenBytesArrived()
    {
        FakeReceiveTransport transport = new();
        ProfileRecordingSink sink = new();
        ReceiveFormattingProfile oldProfile = new(0, Encoding.UTF8.WebName, ReceiveDisplayMode.Str, false);
        ReceiveFormattingProfile newProfile = oldProfile with { Version = 1, EncodingName = Encoding.Latin1.WebName };
        await using ReceivePipeline pipeline = new(
            transport,
            sink,
            new LoadMetrics(),
            ArrayPool<byte>.Shared,
            capacity: 4,
            maximumReadSize: 32,
            oldProfile);
        await pipeline.StartAsync();

        transport.Enqueue("old"u8.ToArray());
        transport.Enqueue("backlog"u8.ToArray());
        transport.RaiseDataAvailable();
        await sink.FirstBlockEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        pipeline.UpdateFormattingProfile(newProfile);
        transport.Enqueue("new"u8.ToArray());
        transport.RaiseDataAvailable();
        sink.ReleaseFirstBlock.TrySetResult();
        await pipeline.StopAsync();

        Assert.Equal([0, 0, 1], sink.ProfileVersions);
    }

    [Fact]
    public async Task ProfileSwitchFlushesOldDecoderBeforeUsingNewEncoding()
    {
        using TemporaryDirectory directory = new();
        LoadMetrics metrics = new();
        await using SessionLogWriter writer = new(
            new SessionLogWriterOptions(directory.Path, "PROFILE", FlushInterval: TimeSpan.FromMilliseconds(25)),
            metrics);
        await writer.StartAsync();
        await using ReceiveSessionSink sink = new(writer, new BudgetedLineStore(1024 * 1024, 4096), metrics);
        ReceiveFormattingProfile utf8 = new(0, Encoding.UTF8.WebName, ReceiveDisplayMode.Str, false);
        ReceiveFormattingProfile latin1 = utf8 with { Version = 1, EncodingName = Encoding.Latin1.WebName };

        using (ReceiveBlock oldBlock = CreateBlock([0xC3], utf8))
        {
            await sink.ProcessAsync(oldBlock, CancellationToken.None);
        }

        using (ReceiveBlock newBlock = CreateBlock([0xE9, (byte)'\n'], latin1))
        {
            await sink.ProcessAsync(newBlock, CancellationToken.None);
        }

        await sink.FlushAsync();
        await writer.StopAsync();

        string text = string.Concat(Directory.GetFiles(directory.Path, "*.txt").Order().Select(File.ReadAllText));
        Assert.Equal("\uFFFDé\r\n", text);
        Assert.Equal(2, metrics.Snapshot().FormattedLogBlocks);
    }

    private static ReceiveBlock CreateBlock(byte[] payload, ReceiveFormattingProfile profile)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(payload.Length);
        payload.CopyTo(buffer, 0);
        return new ReceiveBlock(ArrayPool<byte>.Shared, buffer, payload.Length, DateTimeOffset.UnixEpoch, profile);
    }

    private sealed class ProfileRecordingSink : IReceiveBlockSink
    {
        public TaskCompletionSource FirstBlockEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseFirstBlock { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<long> ProfileVersions { get; } = [];

        public async ValueTask ProcessAsync(ReceiveBlock block, CancellationToken cancellationToken)
        {
            if (ProfileVersions.Count == 0)
            {
                FirstBlockEntered.TrySetResult();
                await ReleaseFirstBlock.Task.WaitAsync(cancellationToken);
            }

            ProfileVersions.Add(block.FormattingProfile.Version);
        }
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

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory() => Path = Directory.CreateTempSubdirectory("ducom-profile-").FullName;

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
