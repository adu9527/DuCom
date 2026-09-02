using System.Buffers;
using DuCom.Core.Parsing;

namespace DuCom.Core.Pipeline;

public sealed class ReceiveBlock : IDisposable
{
    private readonly ArrayPool<byte> _pool;
    private byte[]? _buffer;

    internal ReceiveBlock(
        ArrayPool<byte> pool,
        byte[] buffer,
        int length,
        DateTimeOffset receivedAtUtc,
        ReceiveFormattingProfile formattingProfile)
    {
        _pool = pool;
        _buffer = buffer;
        Length = length;
        ReceivedAtUtc = receivedAtUtc;
        FormattingProfile = formattingProfile ?? throw new ArgumentNullException(nameof(formattingProfile));
    }

    public int Length { get; }

    public DateTimeOffset ReceivedAtUtc { get; }

    public ReceiveFormattingProfile FormattingProfile { get; }

    public ReadOnlyMemory<byte> Memory => (_buffer ?? throw new ObjectDisposedException(nameof(ReceiveBlock))).AsMemory(0, Length);

    public void Dispose()
    {
        byte[]? buffer = Interlocked.Exchange(ref _buffer, null);
        if (buffer is not null)
        {
            _pool.Return(buffer);
        }
    }
}

public interface IReceiveBlockSink
{
    ValueTask ProcessAsync(ReceiveBlock block, CancellationToken cancellationToken);
}
