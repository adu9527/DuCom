using DuCom.Core.Protocols;
using DuCom.Core.Protocols.Custom;
using DuCom.Core.Protocols.Modbus;
using DuCom.Core.Sessions;
using DuCom.ViewModels;

namespace DuCom.Services;

/// <summary>Decodes owned RX/TX traffic subscription blocks away from the transport and UI threads.</summary>
public sealed class ProtocolDecoderService : IDisposable
{
    private const int MaximumBlocks = 2048;
    private const int MaximumBytes = 32 * 1024 * 1024;
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(5);
    private readonly ProtocolFrameStore _frames = new(50_000);
    private readonly object _gate = new();
    private DecoderRun? _run;
    private long _decoderGeneration;
    private long _receivedBytes;
    private long _droppedBlocks;
    private long _droppedBytes;
    private long _gapCount;
    private string _state = "Stopped";
    private bool _paused;
    private int _disposed;

    public void Start(SessionViewModel session, ProtocolDecoderProfile profile)
    {
        ArgumentNullException.ThrowIfNull(session);
        Start(session.WorkspaceSession.RawTaps, profile);
    }

    internal void Start(SessionRawTapHub rawTaps, ProtocolDecoderProfile profile)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(rawTaps);
        ArgumentNullException.ThrowIfNull(profile);
        Stop();

        Func<IProtocolDecoder> decoderFactory = CreateDecoderFactory(profile);
        RawTrafficSubscription subscription = rawTaps.Subscribe(new RawTrafficSubscriptionOptions
        {
            Id = $"protocol-decoder-{GetHashCode():X}-{Guid.NewGuid():N}",
            MaximumBlocks = MaximumBlocks,
            MaximumBytes = MaximumBytes,
        });
        DecoderRun run = new(subscription, decoderFactory);
        lock (_gate)
        {
            if (_disposed != 0)
            {
                subscription.Dispose();
                throw new ObjectDisposedException(nameof(ProtocolDecoderService));
            }

            _run = run;
            _state = "Running";
            run.Pump = Task.Run(() => ProcessAsync(run));
        }
    }

    public void Stop()
    {
        DecoderRun? run;
        lock (_gate)
        {
            run = _run;
            if (run is null)
            {
                if (_disposed == 0) _state = "Stopped";
                return;
            }

            _state = "Stopping";
        }

        if (run.BeginShutdown())
        {
            run.Subscription.Dispose();
            run.Cancellation.Cancel();
        }
        try
        {
            run.Pump?.Wait(ShutdownTimeout);
        }
        catch (AggregateException exception) when (exception.InnerExceptions.All(item => item is OperationCanceledException))
        {
        }

        lock (_gate)
        {
            if (ReferenceEquals(_run, run))
            {
                _run = null;
                _state = _disposed == 0 ? "Stopped" : "Disposed";
            }
        }

        run.DisposeCancellation();
    }

    public bool IsPaused
    {
        get => Volatile.Read(ref _paused);
        set => Volatile.Write(ref _paused, value);
    }

    public void Clear()
    {
        _frames.Clear();
        lock (_gate)
        {
            if (_run is { } run)
            {
                run.DroppedBlocksBaseline = Interlocked.Read(ref run.TotalDroppedBlocks);
                run.DroppedBytesBaseline = Interlocked.Read(ref run.TotalDroppedBytes);
            }
        }
        Interlocked.Exchange(ref _receivedBytes, 0);
        Interlocked.Exchange(ref _droppedBlocks, 0);
        Interlocked.Exchange(ref _droppedBytes, 0);
        Interlocked.Exchange(ref _gapCount, 0);
    }

    public ProtocolDecoderSnapshot Snapshot() => new(
        _frames.Snapshot(),
        Interlocked.Read(ref _receivedBytes),
        Interlocked.Read(ref _droppedBlocks),
        Interlocked.Read(ref _droppedBytes),
        Interlocked.Read(ref _gapCount),
        Volatile.Read(ref _state));

    private async Task ProcessAsync(DecoderRun run)
    {
        try
        {
            while (true)
            {
                RawTrafficSnapshot snapshot = await run.Subscription.ReadAsync(run.Cancellation.Token).ConfigureAwait(false);
                ProcessSnapshot(run, snapshot);
                if (snapshot.Fault is not null)
                {
                    SetState(run, "Faulted");
                    Program.DiagnosticLog?.Error($"Protocol traffic subscription failed: {snapshot.Fault.Message}");
                    return;
                }

                if (snapshot.IsDisposed) return;
            }
        }
        catch (OperationCanceledException) when (run.Cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SetState(run, "Faulted");
            Program.DiagnosticLog?.Error("Protocol decoder pump failed.", exception);
        }
        finally
        {
            FlushDirections(run, "The protocol decoder stopped.", useGap: false);
        }
    }

    private void ProcessSnapshot(DecoderRun run, RawTrafficSnapshot snapshot)
    {
        RawTrafficGap? gap = snapshot.Gap;
        foreach (RawTrafficRecord record in snapshot.Records)
        {
            if (gap is not null &&
                (record.Cursor.Generation != gap.Cursor.Generation || record.Cursor.Sequence > gap.Cursor.Sequence))
            {
                ProcessGap(run, gap);
                gap = null;
            }

            ProcessRecord(run, record);
        }

        if (gap is not null) ProcessGap(run, gap);
        if (!IsCurrent(run)) return;
        Interlocked.Exchange(ref run.TotalDroppedBlocks, snapshot.TotalDroppedBlocks);
        Interlocked.Exchange(ref run.TotalDroppedBytes, snapshot.TotalDroppedBytes);
        Interlocked.Exchange(ref _droppedBlocks, Math.Max(0, snapshot.TotalDroppedBlocks - Interlocked.Read(ref run.DroppedBlocksBaseline)));
        Interlocked.Exchange(ref _droppedBytes, Math.Max(0, snapshot.TotalDroppedBytes - Interlocked.Read(ref run.DroppedBytesBaseline)));
    }

    private void ProcessRecord(DecoderRun run, RawTrafficRecord record)
    {
        if (!IsCurrent(run)) return;
        if (run.SourceGeneration != record.Cursor.Generation)
        {
            if (run.SourceGeneration.HasValue)
                FlushDirections(run, "The transport runtime generation changed.", useGap: true);
            run.Reset(record.Cursor.Generation, Interlocked.Increment(ref _decoderGeneration));
        }

        DecoderDirectionState state = record.Direction == RawTrafficDirection.Rx ? run.Rx : run.Tx;
        ProtocolTrafficDirection direction = record.Direction == RawTrafficDirection.Rx
            ? ProtocolTrafficDirection.Rx
            : ProtocolTrafficDirection.Tx;
        ProtocolDecoderContext context = new(
            run.DecoderGeneration,
            record.Cursor.Sequence,
            record.ByteOffset,
            record.TimestampUtc,
            direction,
            record.Cursor.Generation);
        state.LastContext = context;
        state.EndOffset = record.ByteOffset + record.Bytes.Length;
        Interlocked.Add(ref _receivedBytes, record.Bytes.Length);
        Append(run, state.Decoder.Push(record.Bytes.Span, context));
    }

    private void ProcessGap(DecoderRun run, RawTrafficGap gap)
    {
        if (!IsCurrent(run)) return;
        Interlocked.Increment(ref _gapCount);
        if (run.SourceGeneration == gap.Cursor.Generation)
        {
            FlushDirections(run, gap.Reason, useGap: true, gap.Cursor);
        }
    }

    private void FlushDirections(DecoderRun run, string reason, bool useGap, RawTrafficCursor? cursor = null)
    {
        if (!IsCurrent(run) || !run.SourceGeneration.HasValue) return;
        FlushDirection(run, run.Rx, ProtocolTrafficDirection.Rx, reason, useGap, cursor);
        FlushDirection(run, run.Tx, ProtocolTrafficDirection.Tx, reason, useGap, cursor);
    }

    private void FlushDirection(
        DecoderRun run,
        DecoderDirectionState state,
        ProtocolTrafficDirection direction,
        string reason,
        bool useGap,
        RawTrafficCursor? cursor)
    {
        long offset = direction == ProtocolTrafficDirection.Rx ? cursor?.RxByteOffset ?? state.EndOffset : cursor?.TxByteOffset ?? state.EndOffset;
        ProtocolDecoderContext context = state.LastContext with
        {
            Generation = run.DecoderGeneration,
            Sequence = cursor?.Sequence ?? state.LastContext.Sequence,
            StartByteOffset = offset,
            TimestampUtc = DateTimeOffset.UtcNow,
            Direction = direction,
            SourceGeneration = run.SourceGeneration!.Value,
        };
        Append(run, useGap ? state.Decoder.Gap(context, reason) : state.Decoder.Flush(context));
        state.LastContext = context;
    }

    private void Append(DecoderRun run, IReadOnlyList<ProtocolFrame> frames)
    {
        if (!IsCurrent(run)) return;
        foreach (ProtocolFrame frame in frames) _frames.Append(frame);
    }

    private bool IsCurrent(DecoderRun run)
    {
        lock (_gate) return ReferenceEquals(_run, run);
    }

    private void SetState(DecoderRun run, string state)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_run, run)) _state = state;
        }
    }

    private static Func<IProtocolDecoder> CreateDecoderFactory(ProtocolDecoderProfile profile) =>
        profile.Type == "customBinary" && profile.Custom is not null
            ? () => new CustomBinaryDecoder(profile.Custom)
            : () => new ModbusRtuDecoder(new ModbusRtuDecoderOptions(InterFrameGap: TimeSpan.FromMilliseconds(4)));

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Stop();
        Volatile.Write(ref _state, "Disposed");
        GC.SuppressFinalize(this);
    }

    private sealed class DecoderRun(RawTrafficSubscription subscription, Func<IProtocolDecoder> decoderFactory)
    {
        public RawTrafficSubscription Subscription { get; } = subscription;
        public CancellationTokenSource Cancellation { get; } = new();
        public Func<IProtocolDecoder> DecoderFactory { get; } = decoderFactory;
        public DecoderDirectionState Rx { get; private set; } = new(decoderFactory(), ProtocolTrafficDirection.Rx);
        public DecoderDirectionState Tx { get; private set; } = new(decoderFactory(), ProtocolTrafficDirection.Tx);
        public Task? Pump { get; set; }
        public Guid? SourceGeneration { get; private set; }
        public long DecoderGeneration { get; private set; }
        public long TotalDroppedBlocks;
        public long TotalDroppedBytes;
        public long DroppedBlocksBaseline;
        public long DroppedBytesBaseline;
        private int _shutdownStarted;
        private int _cancellationDisposed;

        public bool BeginShutdown() => Interlocked.Exchange(ref _shutdownStarted, 1) == 0;

        public void DisposeCancellation()
        {
            if (Interlocked.Exchange(ref _cancellationDisposed, 1) == 0) Cancellation.Dispose();
        }

        public void Reset(Guid sourceGeneration, long decoderGeneration)
        {
            SourceGeneration = sourceGeneration;
            DecoderGeneration = decoderGeneration;
            Rx = new DecoderDirectionState(DecoderFactory(), ProtocolTrafficDirection.Rx);
            Tx = new DecoderDirectionState(DecoderFactory(), ProtocolTrafficDirection.Tx);
        }
    }

    private sealed class DecoderDirectionState(IProtocolDecoder decoder, ProtocolTrafficDirection direction)
    {
        public IProtocolDecoder Decoder { get; } = decoder;
        public ProtocolDecoderContext LastContext { get; set; } = new(0, 0, 0, DateTimeOffset.UtcNow, direction);
        public long EndOffset { get; set; }
    }
}

public sealed record ProtocolDecoderSnapshot(
    ProtocolFrameSnapshot Frames,
    long ReceivedBytes,
    long DroppedBlocks,
    long DroppedBytes,
    long GapCount,
    string State);
