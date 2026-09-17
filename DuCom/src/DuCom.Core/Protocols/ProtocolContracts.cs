using System.Collections.ObjectModel;

namespace DuCom.Core.Protocols;

public enum ProtocolFrameKind
{
    Data,
    Noise,
    Incomplete,
}

public enum ProtocolIntegrityStatus
{
    NotChecked,
    Valid,
    Invalid,
    Missing,
}

public enum ProtocolDiagnosticSeverity
{
    Information,
    Warning,
    Error,
}

public enum ProtocolTrafficDirection
{
    Rx,
    Tx,
}

public sealed record ProtocolFrameSummary(
    string Protocol,
    string Type,
    string Text,
    ProtocolFrameKind Kind = ProtocolFrameKind.Data);

public sealed record ProtocolField(
    string Name,
    object? Value,
    int ByteOffset,
    int ByteLength,
    string? Unit = null,
    string? Description = null,
    string? ByteOrder = null,
    ProtocolDiagnosticSeverity Severity = ProtocolDiagnosticSeverity.Information,
    IReadOnlyList<ProtocolField>? Children = null);

public sealed record IntegrityCheckResult(
    ProtocolIntegrityStatus Status,
    string Algorithm,
    ulong? Expected,
    ulong? Calculated,
    int FieldOffset,
    int FieldLength,
    int CoveredOffset,
    int CoveredLength);

public sealed record ProtocolDiagnostic(
    string Code,
    string Message,
    ProtocolDiagnosticSeverity Severity = ProtocolDiagnosticSeverity.Warning,
    string? Path = null,
    int? ByteOffset = null);

public readonly record struct ProtocolDecoderContext(
    long Generation,
    long Sequence,
    long StartByteOffset,
    DateTimeOffset TimestampUtc,
    ProtocolTrafficDirection Direction = ProtocolTrafficDirection.Rx,
    Guid SourceGeneration = default)
{
    public void Deconstruct(
        out long generation,
        out long sequence,
        out long startByteOffset,
        out DateTimeOffset timestampUtc)
    {
        generation = Generation;
        sequence = Sequence;
        startByteOffset = StartByteOffset;
        timestampUtc = TimestampUtc;
    }
}

public sealed class ProtocolFrame
{
    private readonly byte[] _rawBytes;
    private readonly Lazy<IReadOnlyList<ProtocolField>> _fields;

    public ProtocolFrame(
        long decoderFrameId,
        long generation,
        long firstSequence,
        long lastSequence,
        long startByteOffset,
        DateTimeOffset timestampUtc,
        ReadOnlySpan<byte> rawBytes,
        ProtocolFrameSummary summary,
        IntegrityCheckResult integrity,
        IEnumerable<ProtocolDiagnostic>? diagnostics = null,
        Func<IReadOnlyList<ProtocolField>>? fieldsFactory = null,
        ProtocolTrafficDirection direction = ProtocolTrafficDirection.Rx,
        Guid sourceGeneration = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(decoderFrameId);
        ArgumentOutOfRangeException.ThrowIfNegative(generation);
        ArgumentOutOfRangeException.ThrowIfNegative(firstSequence);
        ArgumentOutOfRangeException.ThrowIfNegative(lastSequence);
        ArgumentOutOfRangeException.ThrowIfNegative(startByteOffset);
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(integrity);

        DecoderFrameId = decoderFrameId;
        Generation = generation;
        FirstSequence = firstSequence;
        LastSequence = lastSequence;
        StartByteOffset = startByteOffset;
        TimestampUtc = timestampUtc;
        Direction = direction;
        SourceGeneration = sourceGeneration;
        _rawBytes = rawBytes.ToArray();
        Summary = summary;
        Integrity = integrity;
        Diagnostics = Freeze(diagnostics ?? []);
        _fields = new Lazy<IReadOnlyList<ProtocolField>>(
            () => Freeze(fieldsFactory?.Invoke() ?? []),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public long DecoderFrameId { get; }

    public long Generation { get; }

    public long FirstSequence { get; }

    public long LastSequence { get; }

    public long StartByteOffset { get; }

    public long EndByteOffset => StartByteOffset + _rawBytes.Length;

    public DateTimeOffset TimestampUtc { get; }

    public ProtocolTrafficDirection Direction { get; }

    public Guid SourceGeneration { get; }

    public int Length => _rawBytes.Length;

    public ProtocolFrameSummary Summary { get; }

    public IntegrityCheckResult Integrity { get; }

    public IReadOnlyList<ProtocolDiagnostic> Diagnostics { get; }

    public IReadOnlyList<ProtocolField> Fields => _fields.Value;

    public byte[] GetRawBytes() => _rawBytes.ToArray();

    internal ReadOnlySpan<byte> RawSpan => _rawBytes;

    private static ReadOnlyCollection<T> Freeze<T>(IEnumerable<T> values) =>
        new ReadOnlyCollection<T>(values.ToArray());
}

public interface IProtocolDecoder
{
    IReadOnlyList<ProtocolFrame> Push(ReadOnlySpan<byte> bytes, ProtocolDecoderContext context);

    IReadOnlyList<ProtocolFrame> Gap(ProtocolDecoderContext context, string reason = "Input gap");

    IReadOnlyList<ProtocolFrame> Flush(ProtocolDecoderContext context);
}
