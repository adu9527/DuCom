using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace DuCom.Plugins.Production;

public static class ProtocolCodec
{
    public static int CrcSize(ChecksumKind kind) => kind == ChecksumKind.None ? 0 : kind is ChecksumKind.Crc16Modbus or ChecksumKind.Crc16CcittFalse ? 2 : 1;

    public static byte[] BuildFrame(ProtocolSettings p, ProductionCommand c, byte[]? sourceResponse = null)
    {
        byte[] payload = Hex.Parse(c.Hex);
        if (c.ResponseSourceId.Length > 0 && sourceResponse is not null)
        {
            int end = sourceResponse.Length - CrcSize(p.Checksum);
            if (c.ResponseSourceOffset > end - c.ResponseSourceLength) throw new InvalidDataException("来源应答长度不足");
            payload = [.. payload, .. sourceResponse.AsSpan(c.ResponseSourceOffset, c.ResponseSourceLength)];
        }
        if (c.RawFrame) return payload;
        int size = p.PayloadOffset + payload.Length + CrcSize(p.Checksum);
        int length = size - p.LengthAdjustment;
        if (c.TxLength >= 0 && c.ResponseSourceId.Length == 0 && c.TxLength != length) throw new InvalidDataException($"发送长度应为 {length}，填写为 {c.TxLength}");
        if (length < 0 || length > (p.LengthSize == 1 ? 255 : 65535)) throw new InvalidDataException("长度字段溢出");
        byte[] frame = new byte[size];
        Hex.Parse(p.TxHeader).CopyTo(frame, 0);
        if (p.LengthSize == 1) frame[p.LengthOffset] = (byte)length;
        else if (p.LengthLittleEndian) BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(p.LengthOffset), (ushort)length);
        else BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(p.LengthOffset), (ushort)length);
        payload.CopyTo(frame, p.PayloadOffset);
        WriteChecksum(p, frame);
        return frame;
    }

    public static ushort Checksum(ChecksumKind kind, ReadOnlySpan<byte> data)
    {
        ushort crc = kind is ChecksumKind.Crc16Modbus or ChecksumKind.Crc16CcittFalse ? (ushort)0xFFFF : (ushort)0;
        foreach (byte value in data)
        {
            if (kind == ChecksumKind.Sum8) { crc = (byte)(crc + value); continue; }
            if (kind == ChecksumKind.Xor8) { crc ^= value; continue; }
            if (kind == ChecksumKind.Crc16CcittFalse)
            {
                crc ^= (ushort)(value << 8);
                for (int i = 0; i < 8; i++) crc = (ushort)((crc << 1) ^ ((crc & 0x8000) != 0 ? 0x1021 : 0));
            }
            else if (kind is ChecksumKind.Crc8Maxim or ChecksumKind.Crc16Modbus)
            {
                crc ^= value;
                int polynomial = kind == ChecksumKind.Crc8Maxim ? 0x8C : 0xA001;
                for (int i = 0; i < 8; i++) crc = (ushort)((crc >> 1) ^ ((crc & 1) != 0 ? polynomial : 0));
            }
            else if (kind == ChecksumKind.Crc8)
            {
                crc ^= value;
                for (int i = 0; i < 8; i++) crc = (byte)((crc << 1) ^ ((crc & 0x80) != 0 ? 7 : 0));
            }
        }
        return crc;
    }

    private static ReadOnlySpan<byte> Coverage(ProtocolSettings p, byte[] frame)
    {
        int end = frame.Length - CrcSize(p.Checksum) - p.CrcEndTrim;
        if (p.CrcStart > end) throw new InvalidDataException("CRC 覆盖范围无效");
        return frame.AsSpan(p.CrcStart, end - p.CrcStart);
    }

    public static void WriteChecksum(ProtocolSettings p, byte[] frame)
    {
        int size = CrcSize(p.Checksum);
        if (size == 0) return;
        ushort crc = Checksum(p.Checksum, Coverage(p, frame));
        if (size == 1) frame[^1] = (byte)crc;
        else if (p.CrcLittleEndian) BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(frame.Length - 2), crc);
        else BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(frame.Length - 2), crc);
    }

    public static bool CheckChecksum(ProtocolSettings p, byte[] frame)
    {
        int size = CrcSize(p.Checksum);
        if (size == 0) return true;
        ushort expected = Checksum(p.Checksum, Coverage(p, frame));
        return size == 1 ? frame[^1] == expected : (p.CrcLittleEndian
            ? BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(frame.Length - 2))
            : BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(frame.Length - 2))) == expected;
    }

    public static bool Matches(ProductionCommand c, byte[] frame)
    {
        byte[] match = Hex.Parse(c.ResponseMatch);
        return c.MatchOffset + match.Length <= frame.Length && frame.AsSpan(c.MatchOffset, match.Length).SequenceEqual(match);
    }

    public static string Decode(ProductionCommand c, byte[] frame)
    {
        if (c.StatusOffset >= 0 && (c.StatusOffset >= frame.Length || frame[c.StatusOffset] != c.StatusValue))
            throw new InvalidDataException(c.StatusOffset >= frame.Length ? "状态字段越界" : $"设备状态异常：{frame[c.StatusOffset]:X2}");
        if (c.Fields.Count == 0) return "已应答";
        return string.Join(" · ", c.Fields.Select(f => (c.Fields.Count > 1 ? f.Name + "：" : "") + DecodeField(f, frame)));
    }

    private static string DecodeField(ResponseField f, byte[] frame)
    {
        int size = f.Type switch { FieldType.Mac => 6, FieldType.UInt8 => 1, FieldType.UInt16 or FieldType.Int16 => 2, FieldType.UInt32 or FieldType.Int32 => 4, _ => f.Length };
        if (f.ToEnd) size = frame.Length - f.Offset - 1;
        if (size < 0) throw new InvalidDataException($"{f.Name}：解析越界");
        if (f.Offset > frame.Length - size) throw new InvalidDataException($"{f.Name}：解析越界");
        byte[] bytes = frame.AsSpan(f.Offset, size).ToArray();
        if (f.Reverse) Array.Reverse(bytes);
        string value;
        if (f.Type == FieldType.Mac) value = string.Join(":", bytes.Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));
        else if (f.Type == FieldType.Hex) value = Hex.Format(bytes);
        else if (f.Type == FieldType.Ascii) value = Encoding.ASCII.GetString(bytes).TrimEnd('\0');
        else if (f.Type == FieldType.Version) value = string.Join(".", bytes.Select(b => b.ToString(CultureInfo.InvariantCulture)));
        else
        {
            double number = f.Type switch
            {
                FieldType.UInt8 => bytes[0],
                FieldType.UInt16 => f.LittleEndian ? BinaryPrimitives.ReadUInt16LittleEndian(bytes) : BinaryPrimitives.ReadUInt16BigEndian(bytes),
                FieldType.Int16 => f.LittleEndian ? BinaryPrimitives.ReadInt16LittleEndian(bytes) : BinaryPrimitives.ReadInt16BigEndian(bytes),
                FieldType.UInt32 => f.LittleEndian ? BinaryPrimitives.ReadUInt32LittleEndian(bytes) : BinaryPrimitives.ReadUInt32BigEndian(bytes),
                FieldType.Int32 => f.LittleEndian ? BinaryPrimitives.ReadInt32LittleEndian(bytes) : BinaryPrimitives.ReadInt32BigEndian(bytes),
                _ => 0,
            };
            value = (number * f.Scale).ToString("0.###", CultureInfo.InvariantCulture);
        }
        return value + (f.Unit.Length == 0 ? "" : f.Unit == "%" ? "%" : " " + f.Unit);
    }
}

// One decoder per physical connection; never assume a serial callback is a whole frame.
public sealed class FrameDecoder(ProtocolSettings settings)
{
    private readonly List<byte> _buffer = [];
    public void Clear() => _buffer.Clear();
    public IReadOnlyList<byte[]> Feed(byte[] data, int fixedLength = 0)
    {
        _buffer.AddRange(data);
        List<byte[]> frames = [];
        byte[] header = Hex.Parse(settings.RxHeader);
        while (_buffer.Count >= header.Length)
        {
            if (!_buffer.Take(header.Length).SequenceEqual(header)) { _buffer.RemoveAt(0); continue; }
            int required = settings.LengthOffset + settings.LengthSize;
            if (_buffer.Count < required) break;
            int length = fixedLength > 0 ? fixedLength : settings.LengthSize == 1 ? _buffer[settings.LengthOffset] : settings.LengthLittleEndian
                ? _buffer[settings.LengthOffset] | (_buffer[settings.LengthOffset + 1] << 8)
                : (_buffer[settings.LengthOffset] << 8) | _buffer[settings.LengthOffset + 1];
            if (fixedLength == 0) length += settings.LengthAdjustment;
            if (length < Math.Max(required, header.Length) + ProtocolCodec.CrcSize(settings.Checksum) || length > 4096) { _buffer.RemoveAt(0); continue; }
            if (_buffer.Count < length) break;
            frames.Add(_buffer.GetRange(0, length).ToArray());
            _buffer.RemoveRange(0, length);
        }
        if (_buffer.Count > 8192) _buffer.Clear();
        return frames;
    }
}
