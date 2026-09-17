using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Text;

namespace DuCom.Core.Protocols.Custom;

public sealed class CustomBinaryDecoder : IProtocolDecoder
{
    private readonly CustomBinaryProfile _profile;
    private readonly CustomBinaryProfileLimits _limits;
    private readonly List<BufferedByte> _buffer = [];
    private long _nextFrameId = 1;

    public CustomBinaryDecoder(CustomBinaryProfile profile, CustomBinaryProfileLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _limits = limits ?? new CustomBinaryProfileLimits();
        CustomBinaryProfileValidationResult validation = CustomBinaryProfileValidator.Validate(profile, _limits);
        if (!validation.IsValid)
        {
            throw new ArgumentException(string.Join("; ", validation.Diagnostics.Select(item => $"{item.Path}: {item.Message}")), nameof(profile));
        }

        _profile = profile;
    }

    public IReadOnlyList<ProtocolFrame> Push(ReadOnlySpan<byte> bytes, ProtocolDecoderContext context)
    {
        for (int index = 0; index < bytes.Length; index++)
        {
            _buffer.Add(new BufferedByte(
                bytes[index],
                context.Generation,
                context.Sequence,
                context.StartByteOffset + index,
                context.TimestampUtc,
                context.Direction,
                context.SourceGeneration));
        }

        List<ProtocolFrame> output = [];
        Drain(output, final: false);
        return Freeze(output);
    }

    public IReadOnlyList<ProtocolFrame> Gap(ProtocolDecoderContext context, string reason = "Input gap")
    {
        if (_buffer.Count == 0) return [];
        ProtocolFrame incomplete = CreateNonData(_buffer.Count, ProtocolFrameKind.Incomplete, "custom.input-gap", reason);
        _buffer.Clear();
        return new ReadOnlyCollection<ProtocolFrame>([incomplete]);
    }

    public IReadOnlyList<ProtocolFrame> Flush(ProtocolDecoderContext context)
    {
        List<ProtocolFrame> output = [];
        Drain(output, final: true);
        return Freeze(output);
    }

    private void Drain(List<ProtocolFrame> output, bool final)
    {
        int scans = 0;
        while (_buffer.Count > 0 && scans < _limits.MaximumResyncBytesPerPush)
        {
            scans++;
            if (_profile.Sync is { Bytes.Length: > 0 } sync && !StartsWith(sync.Bytes))
            {
                int next = FindSequence(sync.Bytes, 1);
                int discard = next >= 0 ? next : Math.Max(0, _buffer.Count - sync.Bytes.Length + 1);
                if (discard == 0) break;
                output.Add(CreateNonData(discard, ProtocolFrameKind.Noise, "custom.sync", "Skipped bytes before the synchronization marker."));
                _buffer.RemoveRange(0, discard);
                continue;
            }

            FrameProbe probe = ProbeFrame();
            if (probe.Status == ProbeStatus.NeedMore) break;
            if (probe.Status == ProbeStatus.Invalid)
            {
                output.Add(CreateNonData(1, ProtocolFrameKind.Noise, "custom.length", probe.Error ?? "Invalid frame length."));
                _buffer.RemoveAt(0);
                continue;
            }

            int wireLength = probe.WireLength;
            byte[] wire = Copy(_buffer, wireLength);
            byte[] decoded = DecodeEscapes(wire, out bool danglingEscape);
            if (danglingEscape)
            {
                if (!final) break;
                output.Add(CreateNonData(wireLength, ProtocolFrameKind.Incomplete, "custom.escape", "Frame ended with an incomplete escape sequence."));
                _buffer.RemoveRange(0, wireLength);
                continue;
            }

            if (decoded.Length > _profile.MaximumFrameLength)
            {
                output.Add(CreateNonData(1, ProtocolFrameKind.Noise, "custom.frame-too-large", "Decoded frame exceeds maximumFrameLength."));
                _buffer.RemoveAt(0);
                continue;
            }

            output.Add(CreateFrame(wireLength, decoded));
            _buffer.RemoveRange(0, wireLength);
        }

        if (scans >= _limits.MaximumResyncBytesPerPush && _buffer.Count > 0)
        {
            int keep = Math.Max((_profile.Sync?.Bytes.Length ?? 1) - 1, 0);
            int discard = Math.Max(1, _buffer.Count - keep);
            output.Add(CreateNonData(discard, ProtocolFrameKind.Noise, "custom.resync-limit", "Resynchronization work limit reached."));
            _buffer.RemoveRange(0, discard);
        }

        if (_buffer.Count > _profile.MaximumFrameLength * 2)
        {
            int discard = _buffer.Count - _profile.MaximumFrameLength;
            output.Add(CreateNonData(discard, ProtocolFrameKind.Noise, "custom.buffer-overflow", "Buffered input exceeded the configured limit."));
            _buffer.RemoveRange(0, discard);
        }

        if (final && _buffer.Count > 0)
        {
            output.Add(CreateNonData(_buffer.Count, ProtocolFrameKind.Incomplete, "custom.incomplete", "The stream ended with an incomplete custom frame."));
            _buffer.Clear();
        }
    }

    private FrameProbe ProbeFrame()
    {
        if (_profile.FixedLength is int fixedLength)
        {
            int wireLength = WireLengthForDecodedLength(fixedLength);
            return wireLength < 0 ? FrameProbe.NeedMore : FrameProbe.Complete(wireLength);
        }

        if (_profile.Terminator is { } terminator)
        {
            int index = FindDecodedTerminator(terminator.Bytes);
            if (index < 0)
            {
                return _buffer.Count > _profile.MaximumFrameLength * 2
                    ? FrameProbe.Invalid("Terminator was not found within the frame limit.")
                    : FrameProbe.NeedMore;
            }

            return FrameProbe.Complete(index);
        }

        BinaryLengthDefinition length = _profile.Length!;
        if (!TryDecodePrefix(length.Offset + length.Size, out byte[] prefix, out _)) return FrameProbe.NeedMore;
        ulong rawLength = ReadUnsigned(prefix.AsSpan(length.Offset, length.Size), length.ByteOrder);
        long total = checked((long)rawLength + length.Adjustment);
        if (!length.IncludesHeader) total += length.Offset + length.Size;
        if (!length.IncludesChecksum) total += CustomBinaryProfileValidator.GetChecksumSize(_profile.Checksum?.Algorithm);
        if (total <= 0 || total > _profile.MaximumFrameLength) return FrameProbe.Invalid("Length field produced a frame outside the configured limit.");
        int wire = WireLengthForDecodedLength((int)total);
        return wire < 0 ? FrameProbe.NeedMore : FrameProbe.Complete(wire);
    }

    private ProtocolFrame CreateFrame(int wireLength, byte[] decoded)
    {
        BufferedByte first = _buffer[0];
        BufferedByte last = _buffer[wireLength - 1];
        byte[] wire = Copy(_buffer, wireLength);
        if (_profile.Terminator is { IncludeInFrame: false } terminator && decoded.AsSpan().EndsWith(terminator.Bytes))
        {
            decoded = decoded[..^terminator.Bytes.Length];
        }

        IntegrityCheckResult integrity = ValidateChecksum(decoded);
        List<ProtocolDiagnostic> diagnostics = [];
        if (integrity.Status == ProtocolIntegrityStatus.Invalid)
        {
            diagnostics.Add(new ProtocolDiagnostic("custom.checksum.invalid", "Frame checksum does not match the calculated value.", ProtocolDiagnosticSeverity.Error));
        }

        string text = $"{_profile.Name}, {decoded.Length} decoded bytes";
        return new ProtocolFrame(
            _nextFrameId++, first.Generation, first.Sequence, last.Sequence, first.Offset, first.Timestamp,
            wire,
            new ProtocolFrameSummary("Custom Binary", _profile.Name, text),
            integrity,
            diagnostics,
            () => BuildFields(decoded, _profile.Fields),
            first.Direction,
            first.SourceGeneration);
    }

    private IntegrityCheckResult ValidateChecksum(byte[] decoded)
    {
        if (_profile.Checksum is not { } checksum)
        {
            return new IntegrityCheckResult(ProtocolIntegrityStatus.NotChecked, "None", null, null, -1, 0, 0, 0);
        }

        int size = CustomBinaryProfileValidator.GetChecksumSize(checksum.Algorithm);
        int fieldOffset = checksum.FieldOffset ?? decoded.Length - checksum.FieldOffsetFromEnd!.Value;
        if (fieldOffset < 0 || fieldOffset + size > decoded.Length)
        {
            return new IntegrityCheckResult(ProtocolIntegrityStatus.Missing, checksum.Algorithm.ToString(), null, null, fieldOffset, size, 0, 0);
        }

        int coveredLength = checksum.CoveredLength ?? (checksum.CoveredToBeforeChecksum ? fieldOffset - checksum.CoveredFrom : decoded.Length - checksum.CoveredFrom);
        if (checksum.CoveredFrom < 0 || coveredLength < 0 || checksum.CoveredFrom + coveredLength > decoded.Length)
        {
            return new IntegrityCheckResult(ProtocolIntegrityStatus.Missing, checksum.Algorithm.ToString(), null, null, fieldOffset, size, checksum.CoveredFrom, coveredLength);
        }

        ulong expected = ReadUnsigned(decoded.AsSpan(fieldOffset, size), checksum.ByteOrder);
        ReadOnlySpan<byte> covered = decoded.AsSpan(checksum.CoveredFrom, coveredLength);
        ulong calculated = checksum.Algorithm switch
        {
            BinaryChecksumAlgorithm.Crc16Modbus => ProtocolChecksums.Crc16Modbus(covered),
            BinaryChecksumAlgorithm.Crc16Ccitt => ProtocolChecksums.Crc16Ccitt(covered),
            BinaryChecksumAlgorithm.Crc32 => ProtocolChecksums.Crc32(covered),
            BinaryChecksumAlgorithm.Xor => ProtocolChecksums.Xor(covered),
            BinaryChecksumAlgorithm.Sum8 => ProtocolChecksums.Sum8(covered),
            BinaryChecksumAlgorithm.Sum16 => ProtocolChecksums.Sum16(covered),
            _ => 0,
        };
        return new IntegrityCheckResult(
            expected == calculated ? ProtocolIntegrityStatus.Valid : ProtocolIntegrityStatus.Invalid,
            checksum.Algorithm.ToString(), expected, calculated, fieldOffset, size, checksum.CoveredFrom, coveredLength);
    }

    private static ReadOnlyCollection<ProtocolField> BuildFields(byte[] frame, IReadOnlyList<BinaryFieldDefinition> definitions)
    {
        List<ProtocolField> fields = [];
        foreach (BinaryFieldDefinition definition in definitions)
        {
            int size = CustomBinaryProfileValidator.GetFieldSize(definition);
            if (definition.Offset < 0 || size <= 0 || definition.Offset + size > frame.Length)
            {
                fields.Add(new ProtocolField(definition.Name, null, definition.Offset, Math.Max(size, 0), definition.Unit,
                    "Field is outside this frame.", definition.ByteOrder.ToString(), ProtocolDiagnosticSeverity.Error));
                continue;
            }

            ReadOnlySpan<byte> bytes = frame.AsSpan(definition.Offset, size);
            object value = DecodeField(bytes, definition);
            IReadOnlyList<ProtocolField>? children = definition.Children.Count == 0 ? null : BuildFields(frame, definition.Children);
            fields.Add(new ProtocolField(definition.Name, value, definition.Offset, size, definition.Unit, definition.Description,
                definition.ByteOrder.ToString(), Children: children));
        }

        return new ReadOnlyCollection<ProtocolField>(fields);
    }

    private static object DecodeField(ReadOnlySpan<byte> bytes, BinaryFieldDefinition field)
    {
        if (field.Type == BinaryFieldType.Ascii) return Encoding.ASCII.GetString(bytes).TrimEnd('\0');
        if (field.Type == BinaryFieldType.Utf8) return Encoding.UTF8.GetString(bytes).TrimEnd('\0');
        if (field.Type == BinaryFieldType.Float32)
        {
            int bits = (int)ReadUnsigned(bytes, field.ByteOrder);
            return ApplyScale(BitConverter.Int32BitsToSingle(bits), field);
        }
        if (field.Type == BinaryFieldType.Float64)
        {
            long bits = (long)ReadUnsigned(bytes, field.ByteOrder);
            return ApplyScale(BitConverter.Int64BitsToDouble(bits), field);
        }

        ulong unsigned = ReadUnsigned(bytes, field.ByteOrder);
        if (field.Type == BinaryFieldType.Bits)
        {
            ulong mask = field.BitLength == 64 ? ulong.MaxValue : (1UL << field.BitLength) - 1;
            return ApplyScale((unsigned >> field.BitOffset) & mask, field);
        }

        bool signed = field.Type is BinaryFieldType.Int8 or BinaryFieldType.Int16 or BinaryFieldType.Int32 or BinaryFieldType.Int64;
        double numeric = signed ? SignExtend(unsigned, bytes.Length) : unsigned;
        return ApplyScale(numeric, field);
    }

#pragma warning disable CA1859 // Integral values are preserved for consumers when no transform is configured.
    private static object ApplyScale(double value, BinaryFieldDefinition field)
    {
        double scaled = value * field.Scale + field.ValueOffset;
        if (field.Scale == 1 && field.ValueOffset == 0 && scaled >= long.MinValue && scaled <= long.MaxValue && Math.Truncate(scaled) == scaled)
            return (long)scaled;
        return scaled;
    }
#pragma warning restore CA1859

    private static long SignExtend(ulong value, int byteCount) => byteCount switch
    {
        1 => (sbyte)value,
        2 => (short)value,
        4 => (int)value,
        8 => (long)value,
        _ => throw new ArgumentOutOfRangeException(nameof(byteCount)),
    };

    private static ulong ReadUnsigned(ReadOnlySpan<byte> bytes, BinaryByteOrder order)
    {
        ulong value = 0;
        if (order == BinaryByteOrder.BigEndian)
        {
            foreach (byte item in bytes) value = (value << 8) | item;
        }
        else
        {
            for (int index = bytes.Length - 1; index >= 0; index--) value = (value << 8) | bytes[index];
        }

        return value;
    }

    private byte[] DecodeEscapes(byte[] wire, out bool dangling)
    {
        if (_profile.Escape is not { } escape)
        {
            dangling = false;
            return wire;
        }

        List<byte> decoded = new(wire.Length);
        for (int index = 0; index < wire.Length; index++)
        {
            if (index >= escape.StartOffset && wire[index] == escape.EscapeByte)
            {
                if (++index >= wire.Length)
                {
                    dangling = true;
                    return decoded.ToArray();
                }

                decoded.Add((byte)(wire[index] ^ escape.XorMask));
            }
            else
            {
                decoded.Add(wire[index]);
            }
        }

        dangling = false;
        return decoded.ToArray();
    }

    private bool TryDecodePrefix(int decodedLength, out byte[] decoded, out int wireLength)
    {
        List<byte> result = new(decodedLength);
        BinaryEscapeDefinition? escape = _profile.Escape;
        for (int index = 0; index < _buffer.Count; index++)
        {
            byte value = _buffer[index].Value;
            if (escape is not null && index >= escape.StartOffset && value == escape.EscapeByte)
            {
                if (++index >= _buffer.Count) break;
                value = (byte)(_buffer[index].Value ^ escape.XorMask);
            }

            result.Add(value);
            if (result.Count == decodedLength)
            {
                decoded = result.ToArray();
                wireLength = index + 1;
                return true;
            }
        }

        decoded = [];
        wireLength = -1;
        return false;
    }

    private int WireLengthForDecodedLength(int decodedLength) =>
        TryDecodePrefix(decodedLength, out _, out int wireLength) ? wireLength : -1;

    private int FindDecodedTerminator(byte[] terminator)
    {
        List<byte> decoded = [];
        List<bool> escapedValues = [];
        BinaryEscapeDefinition? escape = _profile.Escape;
        for (int index = 0; index < _buffer.Count; index++)
        {
            byte value = _buffer[index].Value;
            bool escapedValue = false;
            if (escape is not null && index >= escape.StartOffset && value == escape.EscapeByte)
            {
                if (++index >= _buffer.Count) return -1;
                value = (byte)(_buffer[index].Value ^ escape.XorMask);
                escapedValue = true;
            }

            decoded.Add(value);
            escapedValues.Add(escapedValue);
            if (decoded.Count >= terminator.Length &&
                decoded.TakeLast(terminator.Length).SequenceEqual(terminator) &&
                escapedValues.TakeLast(terminator.Length).All(item => !item))
                return index + 1;
            if (decoded.Count > _profile.MaximumFrameLength) return -1;
        }

        return -1;
    }

    private bool StartsWith(byte[] marker)
    {
        if (_buffer.Count < marker.Length) return false;
        for (int index = 0; index < marker.Length; index++)
            if (_buffer[index].Value != marker[index]) return false;
        return true;
    }

    private int FindSequence(byte[] marker, int start)
    {
        for (int index = start; index <= _buffer.Count - marker.Length; index++)
        {
            bool match = true;
            for (int markerIndex = 0; markerIndex < marker.Length; markerIndex++)
                match &= _buffer[index + markerIndex].Value == marker[markerIndex];
            if (match) return index;
        }

        return -1;
    }

    private ProtocolFrame CreateNonData(int length, ProtocolFrameKind kind, string code, string message)
    {
        byte[] raw = Copy(_buffer, length);
        BufferedByte first = _buffer[0];
        BufferedByte last = _buffer[length - 1];
        return new ProtocolFrame(
            _nextFrameId++, first.Generation, first.Sequence, last.Sequence, first.Offset, first.Timestamp,
            raw,
            new ProtocolFrameSummary("Custom Binary", kind.ToString(), message, kind),
            new IntegrityCheckResult(ProtocolIntegrityStatus.NotChecked, "None", null, null, -1, 0, 0, 0),
            [new ProtocolDiagnostic(code, message)],
            direction: first.Direction,
            sourceGeneration: first.SourceGeneration);
    }

    private static byte[] Copy(IReadOnlyList<BufferedByte> bytes, int length)
    {
        byte[] result = new byte[length];
        for (int index = 0; index < length; index++) result[index] = bytes[index].Value;
        return result;
    }

    private static ReadOnlyCollection<ProtocolFrame> Freeze(IEnumerable<ProtocolFrame> frames) =>
        new ReadOnlyCollection<ProtocolFrame>(frames.ToArray());

    private enum ProbeStatus { NeedMore, Complete, Invalid }

    private readonly record struct FrameProbe(ProbeStatus Status, int WireLength, string? Error)
    {
        public static FrameProbe NeedMore => new(ProbeStatus.NeedMore, 0, null);
        public static FrameProbe Complete(int wireLength) => new(ProbeStatus.Complete, wireLength, null);
        public static FrameProbe Invalid(string error) => new(ProbeStatus.Invalid, 0, error);
    }

    private readonly record struct BufferedByte(
        byte Value,
        long Generation,
        long Sequence,
        long Offset,
        DateTimeOffset Timestamp,
        ProtocolTrafficDirection Direction,
        Guid SourceGeneration);
}
