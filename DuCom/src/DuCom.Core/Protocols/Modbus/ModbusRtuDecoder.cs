using System.Buffers.Binary;
using System.Collections.ObjectModel;

namespace DuCom.Core.Protocols.Modbus;

public sealed record ModbusRtuDecoderOptions(
    int MaximumFrameLength = 256,
    int MaximumBufferedBytes = 4_096,
    int MaximumResyncBytesPerPush = 4_096,
    TimeSpan? InterFrameGap = null)
{
    public void Validate()
    {
        if (MaximumFrameLength is < 5 or > 65_536)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumFrameLength));
        }

        if (MaximumBufferedBytes < MaximumFrameLength || MaximumBufferedBytes > 1_048_576)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumBufferedBytes));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumResyncBytesPerPush);
        if (InterFrameGap < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(InterFrameGap));
        }
    }
}

public sealed class ModbusRtuDecoder : IProtocolDecoder
{
    private static readonly HashSet<byte> SupportedFunctions = [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x0F, 0x10];
    private readonly ModbusRtuDecoderOptions _options;
    private readonly List<BufferedByte> _buffer = [];
    private long _nextFrameId = 1;
    private DateTimeOffset? _lastTimestamp;

    public ModbusRtuDecoder(ModbusRtuDecoderOptions? options = null)
    {
        _options = options ?? new ModbusRtuDecoderOptions();
        _options.Validate();
    }

    public IReadOnlyList<ProtocolFrame> Push(ReadOnlySpan<byte> bytes, ProtocolDecoderContext context)
    {
        List<ProtocolFrame> output = [];
        if (_buffer.Count > 0 && _options.InterFrameGap is { } gap &&
            _lastTimestamp.HasValue && context.TimestampUtc - _lastTimestamp.Value >= gap)
        {
            Drain(output, final: true);
        }

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

        _lastTimestamp = context.TimestampUtc;
        if (_buffer.Count > _options.MaximumBufferedBytes)
        {
            int excess = _buffer.Count - _options.MaximumBufferedBytes;
            output.Add(CreateNoise(excess, "modbus.buffer-overflow", "Buffered input exceeded the configured limit."));
            _buffer.RemoveRange(0, excess);
        }

        Drain(output, final: false);
        return Freeze(output);
    }

    public IReadOnlyList<ProtocolFrame> Gap(ProtocolDecoderContext context, string reason = "Input gap")
    {
        List<ProtocolFrame> output = [];
        if (_buffer.Count > 0)
        {
            output.Add(CreateIncomplete(_buffer.Count, "modbus.input-gap", reason));
            _buffer.Clear();
        }

        _lastTimestamp = context.TimestampUtc;
        return Freeze(output);
    }

    public IReadOnlyList<ProtocolFrame> Flush(ProtocolDecoderContext context)
    {
        List<ProtocolFrame> output = [];
        Drain(output, final: true);
        _lastTimestamp = context.TimestampUtc;
        return Freeze(output);
    }

    private void Drain(List<ProtocolFrame> output, bool final)
    {
        int scans = 0;
        List<BufferedByte> noise = [];
        while (_buffer.Count >= 5 && scans < _options.MaximumResyncBytesPerPush)
        {
            scans++;
            int length = DetermineLength(_buffer);
            if (length == 0)
            {
                break;
            }

            if (length < 5 || length > _options.MaximumFrameLength)
            {
                noise.Add(_buffer[0]);
                _buffer.RemoveAt(0);
                continue;
            }

            if (_buffer.Count < length)
            {
                if (TryFindValidFrameStart(1, out int validStart))
                {
                    noise.AddRange(_buffer.GetRange(0, validStart));
                    _buffer.RemoveRange(0, validStart);
                    continue;
                }

                break;
            }

            if (!HasValidCrc(length))
            {
                if (_buffer.Count == length || IsValidFrameAt(length))
                {
                    if (noise.Count > 0)
                    {
                        output.Add(CreateNonData(noise, ProtocolFrameKind.Noise, "Noise", "modbus.resync", "Skipped bytes while searching for a valid Modbus RTU frame."));
                        noise.Clear();
                    }

                    output.Add(CreateFrame(length));
                    _buffer.RemoveRange(0, length);
                    continue;
                }

                noise.Add(_buffer[0]);
                _buffer.RemoveAt(0);
                continue;
            }

            if (noise.Count > 0)
            {
                output.Add(CreateNonData(noise, ProtocolFrameKind.Noise, "Noise", "modbus.resync", "Skipped bytes while searching for a valid Modbus RTU frame."));
                noise.Clear();
            }

            output.Add(CreateFrame(length));
            _buffer.RemoveRange(0, length);
        }

        if (noise.Count > 0)
        {
            output.Add(CreateNonData(noise, ProtocolFrameKind.Noise, "Noise", "modbus.resync", "Skipped bytes while searching for a valid Modbus RTU frame."));
        }

        if (scans >= _options.MaximumResyncBytesPerPush && _buffer.Count > 0)
        {
            int discard = Math.Min(_buffer.Count, Math.Max(1, _buffer.Count - 4));
            output.Add(CreateNoise(discard, "modbus.resync-limit", "Resynchronization work limit reached."));
            _buffer.RemoveRange(0, discard);
        }

        if (final && _buffer.Count > 0)
        {
            output.Add(CreateIncomplete(_buffer.Count, "modbus.incomplete", "The stream ended with an incomplete or invalid Modbus RTU frame."));
            _buffer.Clear();
        }
    }

    private static int DetermineLength(IReadOnlyList<BufferedByte> bytes)
    {
        byte function = bytes[1].Value;
        if ((function & 0x80) != 0)
        {
            return SupportedFunctions.Contains((byte)(function & 0x7F)) ? 5 : -1;
        }

        return function switch
        {
            0x01 or 0x02 or 0x03 or 0x04 => DetermineReadLength(bytes),
            0x05 or 0x06 => 8,
            0x0F or 0x10 => DetermineWriteMultipleLength(bytes),
            _ => -1,
        };
    }

    private static int DetermineReadLength(IReadOnlyList<BufferedByte> bytes)
    {
        if (bytes.Count < 3)
        {
            return 0;
        }

        int responseLength = 5 + bytes[2].Value;
        if (responseLength >= 5 && responseLength <= 260 && bytes.Count >= responseLength && HasValidCrc(bytes, responseLength))
        {
            return responseLength;
        }

        if (bytes.Count >= 8 && HasValidCrc(bytes, 8))
        {
            return 8;
        }

        return bytes.Count < Math.Min(responseLength, 8) ? 0 : responseLength;
    }

    private static int DetermineWriteMultipleLength(IReadOnlyList<BufferedByte> bytes)
    {
        if (bytes.Count >= 8 && HasValidCrc(bytes, 8))
        {
            return 8;
        }

        if (bytes.Count < 7)
        {
            return 0;
        }

        return 9 + bytes[6].Value;
    }

    private bool TryFindValidFrameStart(int start, out int found)
    {
        int maximum = Math.Min(_buffer.Count - 5, _options.MaximumResyncBytesPerPush);
        for (int index = start; index <= maximum; index++)
        {
            IReadOnlyList<BufferedByte> tail = _buffer.GetRange(index, _buffer.Count - index);
            int length = DetermineLength(tail);
            if (length >= 5 && length <= tail.Count && length <= _options.MaximumFrameLength && HasValidCrc(tail, length))
            {
                found = index;
                return true;
            }
        }

        found = 0;
        return false;
    }

    private bool HasValidCrc(int length) => HasValidCrc(_buffer, length);

    private bool IsValidFrameAt(int offset)
    {
        if (_buffer.Count - offset < 5) return false;
        IReadOnlyList<BufferedByte> tail = _buffer.GetRange(offset, _buffer.Count - offset);
        int length = DetermineLength(tail);
        return length >= 5 && length <= tail.Count && length <= _options.MaximumFrameLength && HasValidCrc(tail, length);
    }

    private static bool HasValidCrc(IReadOnlyList<BufferedByte> bytes, int length)
    {
        if (length > bytes.Count || length < 3)
        {
            return false;
        }

        byte[] raw = Copy(bytes, length);
        ushort expected = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(length - 2));
        return ProtocolChecksums.Crc16Modbus(raw.AsSpan(0, length - 2)) == expected;
    }

    private ProtocolFrame CreateFrame(int length)
    {
        byte[] raw = Copy(_buffer, length);
        BufferedByte first = _buffer[0];
        BufferedByte last = _buffer[length - 1];
        byte function = raw[1];
        bool exception = (function & 0x80) != 0;
        ushort expected = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(length - 2));
        ushort calculated = ProtocolChecksums.Crc16Modbus(raw.AsSpan(0, length - 2));
        string type = exception ? $"Exception 0x{function & 0x7F:X2}" : FunctionName(function);
        string text = exception
            ? $"Unit {raw[0]}, {type}, code 0x{raw[2]:X2}"
            : $"Unit {raw[0]}, {type}, {length} bytes";
        ProtocolIntegrityStatus status = expected == calculated ? ProtocolIntegrityStatus.Valid : ProtocolIntegrityStatus.Invalid;
        ProtocolDiagnostic[] diagnostics = status == ProtocolIntegrityStatus.Invalid
            ? [new ProtocolDiagnostic("modbus.crc.invalid", "Frame CRC does not match the calculated value.", ProtocolDiagnosticSeverity.Error)]
            : [];
        return new ProtocolFrame(
            _nextFrameId++, first.Generation, first.Sequence, last.Sequence, first.Offset, first.Timestamp,
            raw,
            new ProtocolFrameSummary("Modbus RTU", type, text),
            new IntegrityCheckResult(status, "CRC16/MODBUS", expected, calculated, length - 2, 2, 0, length - 2),
            diagnostics,
            fieldsFactory: () => BuildFields(raw),
            direction: first.Direction,
            sourceGeneration: first.SourceGeneration);
    }

    private ProtocolFrame CreateNoise(int length, string code, string message)
    {
        ProtocolFrame frame = CreateNonData(length, ProtocolFrameKind.Noise, "Noise", code, message);
        return frame;
    }

    private ProtocolFrame CreateIncomplete(int length, string code, string message) =>
        CreateNonData(length, ProtocolFrameKind.Incomplete, "Incomplete", code, message);

    private ProtocolFrame CreateNonData(int length, ProtocolFrameKind kind, string type, string code, string message)
    {
        return CreateNonData(_buffer.GetRange(0, length), kind, type, code, message);
    }

    private ProtocolFrame CreateNonData(IReadOnlyList<BufferedByte> source, ProtocolFrameKind kind, string type, string code, string message)
    {
        byte[] raw = Copy(source, source.Count);
        BufferedByte first = source[0];
        BufferedByte last = source[^1];
        return new ProtocolFrame(
            _nextFrameId++, first.Generation, first.Sequence, last.Sequence, first.Offset, first.Timestamp,
            raw,
            new ProtocolFrameSummary("Modbus RTU", type, message, kind),
            new IntegrityCheckResult(ProtocolIntegrityStatus.NotChecked, "CRC16/MODBUS", null, null, -1, 0, 0, 0),
            [new ProtocolDiagnostic(code, message)],
            direction: first.Direction,
            sourceGeneration: first.SourceGeneration);
    }

    private static ReadOnlyCollection<ProtocolField> BuildFields(byte[] raw)
    {
        List<ProtocolField> fields =
        [
            new("Unit address", raw[0], 0, 1),
            new("Function", $"0x{raw[1]:X2}", 1, 1, Description: FunctionName((byte)(raw[1] & 0x7F))),
        ];
        byte function = raw[1];
        if ((function & 0x80) != 0)
        {
            fields.Add(new ProtocolField("Exception code", $"0x{raw[2]:X2}", 2, 1));
        }
        else if (function is 0x05 or 0x06 || function is 0x0F or 0x10 && raw.Length == 8)
        {
            fields.Add(new ProtocolField("Address", BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(2, 2)), 2, 2, ByteOrder: "bigEndian"));
            fields.Add(new ProtocolField(function is 0x05 or 0x06 ? "Value" : "Quantity", BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(4, 2)), 4, 2, ByteOrder: "bigEndian"));
        }
        else if (function is 0x01 or 0x02 or 0x03 or 0x04 && raw.Length == 8)
        {
            fields.Add(new ProtocolField("Start address", BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(2, 2)), 2, 2, ByteOrder: "bigEndian"));
            fields.Add(new ProtocolField("Quantity", BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(4, 2)), 4, 2, ByteOrder: "bigEndian"));
        }
        else if (function is 0x01 or 0x02 or 0x03 or 0x04)
        {
            fields.Add(new ProtocolField("Byte count", raw[2], 2, 1));
            fields.Add(new ProtocolField("Data", Convert.ToHexString(raw, 3, raw.Length - 5), 3, raw.Length - 5));
        }
        else if (function is 0x0F or 0x10)
        {
            fields.Add(new ProtocolField("Start address", BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(2, 2)), 2, 2, ByteOrder: "bigEndian"));
            fields.Add(new ProtocolField("Quantity", BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(4, 2)), 4, 2, ByteOrder: "bigEndian"));
            fields.Add(new ProtocolField("Byte count", raw[6], 6, 1));
            fields.Add(new ProtocolField("Data", Convert.ToHexString(raw, 7, raw.Length - 9), 7, raw.Length - 9));
        }

        fields.Add(new ProtocolField("CRC", BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(raw.Length - 2)), raw.Length - 2, 2, ByteOrder: "littleEndian"));
        return new ReadOnlyCollection<ProtocolField>(fields);
    }

    private static string FunctionName(byte function) => function switch
    {
        0x01 => "Read Coils",
        0x02 => "Read Discrete Inputs",
        0x03 => "Read Holding Registers",
        0x04 => "Read Input Registers",
        0x05 => "Write Single Coil",
        0x06 => "Write Single Register",
        0x0F => "Write Multiple Coils",
        0x10 => "Write Multiple Registers",
        _ => $"Function 0x{function:X2}",
    };

    private static byte[] Copy(IReadOnlyList<BufferedByte> bytes, int length)
    {
        byte[] result = new byte[length];
        for (int index = 0; index < length; index++)
        {
            result[index] = bytes[index].Value;
        }

        return result;
    }

    private static ReadOnlyCollection<ProtocolFrame> Freeze(IEnumerable<ProtocolFrame> frames) =>
        new ReadOnlyCollection<ProtocolFrame>(frames.ToArray());

    private readonly record struct BufferedByte(
        byte Value,
        long Generation,
        long Sequence,
        long Offset,
        DateTimeOffset Timestamp,
        ProtocolTrafficDirection Direction,
        Guid SourceGeneration);
}
