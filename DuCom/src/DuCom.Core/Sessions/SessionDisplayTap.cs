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
/// <see cref="Publish"/> run on the receive pipeline thread: they must never block, never
/// touch UI objects, and only enqueue work for the UI thread.
/// </summary>
public sealed class SessionDisplayTap
{
    public required string Id { get; init; }

    public required Func<SessionTapDisplayFormat> FormatSelector { get; init; }

    public required Action<string> Publish { get; init; }
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
        lock (_gate)
        {
            if (!_runtimesByTapId.ContainsKey(tap.Id) && _runtimesByTapId.Count >= MaximumTapCount)
            {
                throw new InvalidOperationException($"At most {MaximumTapCount} display taps may be registered per session.");
            }

            _runtimesByTapId[tap.Id] = new TapRuntime(tap);
        }
    }

    public bool Unregister(string tapId)
    {
        lock (_gate)
        {
            return _runtimesByTapId.Remove(tapId);
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

        List<TapPublication> publications = [];
        lock (_gate)
        {
            if (_runtimesByTapId.Count == 0)
            {
                return;
            }

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

                    publications.Add(new TapPublication(runtime, builder.ToString()));
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
            try
            {
                publication.Runtime.Tap.Publish(publication.Payload);
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
        List<TapPublication> publications = [];
        lock (_gate)
        {
            if (_runtimesByTapId.Count == 0)
            {
                return;
            }

            string payload = text + "\r\n";
            foreach (TapRuntime runtime in _runtimesByTapId.Values)
            {
                publications.Add(new TapPublication(runtime, payload));
            }
        }

        foreach (TapPublication publication in publications)
        {
            try
            {
                publication.Runtime.Tap.Publish(publication.Payload);
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
        }
    }

    private readonly record struct TapPublication(TapRuntime Runtime, string Payload);

    private sealed class TapRuntime
    {
        public TapRuntime(SessionDisplayTap tap)
        {
            Tap = tap;
        }

        public SessionDisplayTap Tap { get; }

        public StatefulReceiveFormatter? Formatter { get; set; }

        public ReceiveDisplayMode FormatterMode { get; set; }

        public bool HasEmittedContent { get; set; }

        public bool AwaitsSeparator { get; set; }
    }
}
