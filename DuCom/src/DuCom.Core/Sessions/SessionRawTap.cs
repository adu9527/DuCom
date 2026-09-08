namespace DuCom.Core.Sessions;

/// <summary>
/// One raw receive observer. <see cref="Publish"/> runs on the receive pipeline thread:
/// it must copy the span immediately, never block, and never touch UI objects.
/// </summary>
public sealed class SessionRawTap
{
    public required string Id { get; init; }

    public required Action<ReadOnlyMemory<byte>, DateTimeOffset> Publish { get; init; }
}

/// <summary>
/// Per-session fan-out of raw receive blocks (pre-formatting bytes). Host-internal broker
/// surface for out-of-process observers; adding a hub costs one null check per block.
/// </summary>
public sealed class SessionRawTapHub
{
    private const int MaximumTapCount = 4;
    private readonly object _gate = new();
    private readonly Dictionary<string, SessionRawTap> _taps = new(StringComparer.Ordinal);

    public IDisposable Register(SessionRawTap tap)
    {
        ArgumentNullException.ThrowIfNull(tap);
        lock (_gate)
        {
            if (!_taps.ContainsKey(tap.Id) && _taps.Count >= MaximumTapCount)
            {
                throw new InvalidOperationException($"At most {MaximumTapCount} raw taps may be registered per session.");
            }

            _taps[tap.Id] = tap;
        }

        return new Registration(this, tap.Id);
    }

    public void PublishRaw(ReadOnlyMemory<byte> bytes, DateTimeOffset receivedAtUtc)
    {
        if (bytes.IsEmpty)
        {
            return;
        }

        SessionRawTap[]? targets = null;
        lock (_gate)
        {
            if (_taps.Count == 0)
            {
                return;
            }

            targets = [.. _taps.Values];
        }

        foreach (SessionRawTap tap in targets)
        {
            try
            {
                tap.Publish(bytes, receivedAtUtc);
            }
            catch (Exception)
            {
                lock (_gate)
                {
                    _taps.Remove(tap.Id);
                }
            }
        }
    }

    private sealed class Registration(SessionRawTapHub hub, string id) : IDisposable
    {
        public void Dispose()
        {
            lock (hub._gate)
            {
                hub._taps.Remove(id);
            }
        }
    }
}
