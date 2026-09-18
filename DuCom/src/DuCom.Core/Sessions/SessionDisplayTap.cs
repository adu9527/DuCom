using DuCom.Core.Parsing;
using DuCom.Core.Sending;

namespace DuCom.Core.Sessions;

public enum SessionTapDisplayFormat
{
    Str,
    Hex,
}

/// <summary>
/// One auxiliary display surface (float send window, log filter window) mirroring the
/// session receive/transmit text stream. <see cref="FormatSelector"/> and
/// <see cref="Publish"/> runs on the receive pipeline thread unless bounded asynchronous
/// delivery is enabled. Inline callbacks must never block or touch UI objects.
/// </summary>
public sealed class SessionDisplayTap
{
    public required string Id { get; init; }

    public required Func<SessionTapDisplayFormat> FormatSelector { get; init; }

    public required Action<string> Publish { get; init; }

    /// <summary>Optional timestamp-aware callback for analysis surfaces.</summary>
    public Action<string, DateTimeOffset>? PublishTimestamped { get; init; }

    public bool BoundedAsyncDelivery { get; init; }

    public int MaximumPendingPublications { get; init; } = 256;

    public int MaximumPendingCharacters { get; init; } = 1024 * 1024;
}

/// <summary>
/// Per-session fan-out of the receive stream to registered display taps plus the last-send
/// tracking used by the float-window reply-window rule. Each tap owns a private
/// <see cref="StatefulReceiveFormatter"/> per display format so soft-wrapped lines and
/// timestamps stay identical to the main display while the tap follows its own format.
/// </summary>
public sealed class SessionTapHub
{
    private const int MaximumTapCount = 8;

    private readonly object _gate = new();
    private readonly Dictionary<string, TapRuntime> _runtimesByTapId = new(StringComparer.OrdinalIgnoreCase);
    private SendMode _lastSendMode = SendMode.Str;
    private long _lastSendUtcTicks;

    /// <summary>Registers a tap; duplicate ids replace the previous registration.</summary>
    public void Register(SessionDisplayTap tap)
    {
        ArgumentNullException.ThrowIfNull(tap);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tap.MaximumPendingPublications);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tap.MaximumPendingCharacters);
        lock (_gate)
        {
            if (!_runtimesByTapId.ContainsKey(tap.Id) && _runtimesByTapId.Count >= MaximumTapCount)
            {
                throw new InvalidOperationException($"At most {MaximumTapCount} display taps may be registered per session.");
            }

            if (_runtimesByTapId.Remove(tap.Id, out TapRuntime? previous))
            {
                previous.Deactivate();
            }
            _runtimesByTapId[tap.Id] = new TapRuntime(this, tap);
        }
    }

    public bool Unregister(string tapId)
    {
        lock (_gate)
        {
            if (!_runtimesByTapId.Remove(tapId, out TapRuntime? runtime))
            {
                return false;
            }
            runtime.Deactivate();
            return true;
        }
    }

    public int TapCount
    {
        get
        {
            lock (_gate)
            {
                return _runtimesByTapId.Count;
            }
        }
    }

    /// <summary>Records the send mode and time so taps can apply the reply-window rule.</summary>
    public void NotifySent(SendMode mode)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        lock (_gate)
        {
            _lastSendMode = mode;
            _lastSendUtcTicks = DateTime.UtcNow.Ticks;
        }
    }

    /// <summary>
    /// Reply-window resolution: returns the last send's mode when a send happened within
    /// <paramref name="replyWindowMilliseconds"/>, otherwise <c>null</c> (outside the window).
    /// </summary>
    public SendMode? ResolveReplyWindowFormat(int replyWindowMilliseconds)
    {
        lock (_gate)
        {
            if (_lastSendUtcTicks == 0 || replyWindowMilliseconds <= 0)
            {
                return null;
            }

            long elapsedMilliseconds = (DateTime.UtcNow.Ticks - _lastSendUtcTicks) / TimeSpan.TicksPerMillisecond;
            return elapsedMilliseconds < replyWindowMilliseconds ? _lastSendMode : null;
        }
    }

    /// <summary>Called by the receive sink on the pipeline thread for every accepted block.</summary>
    public void PublishReceive(ReadOnlySpan<byte> bytes, DateTimeOffset receivedAtUtc, ReceiveFormattingProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (bytes.IsEmpty)
        {
            return;
        }

        List<TapPublication> publications;
        lock (_gate)
        {
            if (_runtimesByTapId.Count == 0)
            {
                return;
            }
            publications = new List<TapPublication>(_runtimesByTapId.Count);

            List<TapRuntime>? faultedRuntimes = null;
            foreach (TapRuntime runtime in _runtimesByTapId.Values)
            {
                try
                {
                    ReceiveDisplayMode desiredMode = runtime.Tap.FormatSelector() == SessionTapDisplayFormat.Hex
                        ? ReceiveDisplayMode.Hex
                        : ReceiveDisplayMode.Str;
                    if (runtime.Formatter is null || runtime.FormatterMode != desiredMode)
                    {
                        runtime.Formatter = (profile with { DisplayMode = desiredMode }).CreateFormatter();
                        runtime.FormatterMode = desiredMode;
                        runtime.HasEmittedContent = false;
                        runtime.AwaitsSeparator = false;
                    }

                    IReadOnlyList<FormattedLine> lines = runtime.Formatter.Append(bytes, receivedAtUtc);
                    if (lines.Count == 0)
                    {
                        continue;
                    }

                    System.Text.StringBuilder builder = new();
                    foreach (FormattedLine line in lines)
                    {
                        if (runtime.AwaitsSeparator && runtime.HasEmittedContent)
                        {
                            builder.Append("\r\n");
                        }

                        builder.Append(line.Text);
                        runtime.HasEmittedContent = true;
                        runtime.AwaitsSeparator = !line.IsSoftWrapped;
                    }

                    publications.Add(new TapPublication(runtime, builder.ToString(), lines[0].ReceivedAtUtc));
                }
                catch (Exception)
                {
                    (faultedRuntimes ??= []).Add(runtime);
                }
            }

            if (faultedRuntimes is not null)
            {
                foreach (TapRuntime runtime in faultedRuntimes)
                {
                    RemoveIfCurrent(runtime);
                }
            }
        }

        // Handlers run after the hub lock is released so a window-side lock can never be
        // taken while some other thread holds the hub lock.
        foreach (TapPublication publication in publications)
        {
            if (publication.Runtime.Tap.BoundedAsyncDelivery)
            {
                publication.Runtime.Enqueue(publication.Payload, publication.ReceivedAtUtc);
                continue;
            }
            try
            {
                publication.Runtime.Publish(publication.Payload, publication.ReceivedAtUtc);
            }
            catch (Exception)
            {
                lock (_gate)
                {
                    RemoveIfCurrent(publication.Runtime);
                }
            }
        }
    }

    /// <summary>Called by <see cref="SerialSession.SendAsync"/> after a transmitted record is stored.</summary>
    public void PublishTransmit(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        List<TapPublication> publications;
        lock (_gate)
        {
            if (_runtimesByTapId.Count == 0)
            {
                return;
            }
            publications = new List<TapPublication>(_runtimesByTapId.Count);

            string payload = text + "\r\n";
            foreach (TapRuntime runtime in _runtimesByTapId.Values)
            {
                publications.Add(new TapPublication(runtime, payload, DateTimeOffset.UtcNow));
            }
        }

        foreach (TapPublication publication in publications)
        {
            if (publication.Runtime.Tap.BoundedAsyncDelivery)
            {
                publication.Runtime.Enqueue(publication.Payload, publication.ReceivedAtUtc);
                continue;
            }
            try
            {
                publication.Runtime.Publish(publication.Payload, publication.ReceivedAtUtc);
            }
            catch (Exception)
            {
                lock (_gate)
                {
                    RemoveIfCurrent(publication.Runtime);
                }
            }
        }
    }

    private void RemoveIfCurrent(TapRuntime runtime)
    {
        if (_runtimesByTapId.TryGetValue(runtime.Tap.Id, out TapRuntime? current) && ReferenceEquals(current, runtime))
        {
            _runtimesByTapId.Remove(runtime.Tap.Id);
            runtime.Deactivate();
        }
    }

    private readonly record struct TapPublication(TapRuntime Runtime, string Payload, DateTimeOffset ReceivedAtUtc);

    private sealed class TapRuntime
    {
        private readonly SessionTapHub _owner;
        private readonly object _deliveryGate = new();
        private readonly Queue<(string Payload, DateTimeOffset Timestamp)> _pending = new();
        private int _pendingCharacters;
        private bool _drainScheduled;
        private bool _active = true;

        public TapRuntime(SessionTapHub owner, SessionDisplayTap tap)
        {
            _owner = owner;
            Tap = tap;
        }

        public SessionDisplayTap Tap { get; }

        public StatefulReceiveFormatter? Formatter { get; set; }

        public ReceiveDisplayMode FormatterMode { get; set; }

        public bool HasEmittedContent { get; set; }

        public bool AwaitsSeparator { get; set; }

        public void Enqueue(string payload, DateTimeOffset timestamp)
        {
            lock (_deliveryGate)
            {
                if (!_active)
                {
                    return;
                }

                while (_pending.Count > 0 &&
                    (_pending.Count >= Tap.MaximumPendingPublications ||
                     _pendingCharacters + payload.Length > Tap.MaximumPendingCharacters))
                {
                    (string dropped, _) = _pending.Dequeue();
                    _pendingCharacters -= dropped.Length;
                }

                if (payload.Length > Tap.MaximumPendingCharacters)
                {
                    return;
                }

                _pending.Enqueue((payload, timestamp));
                _pendingCharacters += payload.Length;
                if (_drainScheduled)
                {
                    return;
                }
                _drainScheduled = true;
            }

            _ = Task.Run(DrainAsync);
        }

        public void Deactivate()
        {
            lock (_deliveryGate)
            {
                _active = false;
                _pending.Clear();
                _pendingCharacters = 0;
            }
        }

        public void Publish(string payload, DateTimeOffset timestamp)
        {
            if (Tap.PublishTimestamped is { } publishTimestamped)
                publishTimestamped(payload, timestamp);
            else
                Tap.Publish(payload);
        }

        private Task DrainAsync()
        {
            while (true)
            {
                (string Payload, DateTimeOffset Timestamp) publication;
                lock (_deliveryGate)
                {
                    if (!_active || _pending.Count == 0)
                    {
                        _drainScheduled = false;
                        return Task.CompletedTask;
                    }
                    publication = _pending.Dequeue();
                    _pendingCharacters -= publication.Payload.Length;
                }

                try
                {
                    Publish(publication.Payload, publication.Timestamp);
                }
                catch
                {
                    lock (_owner._gate)
                    {
                        _owner.RemoveIfCurrent(this);
                    }
                    return Task.CompletedTask;
                }
            }
        }
    }
}
