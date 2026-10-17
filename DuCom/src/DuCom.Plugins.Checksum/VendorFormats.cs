using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

namespace DuCom.Plugins.Checksum;

internal sealed record VendorIdentity(string Manufacturer, string FileType, string Code,
    string Chip, bool? ContentVerified, string Note, string BluetoothName = "未识别");

internal static class VendorFormats
{
    private sealed record Entry(ushort Type, ushort Index, ushort Crc, int Offset, int Size, int Allocated, string Name);

    public static VendorIdentity? Read(byte[] file)
    {
        if (file.Length >= 4 && (file.AsSpan(0, 4).SequenceEqual("PRD\0"u8) || file.AsSpan(0, 4).SequenceEqual("UPD\x01"u8)))
            return ReadBluetrum(file);
        if (file.Length >= 64)
        {
            var tail = file.AsSpan(file.Length - 16, 16);
            if (tail.SequenceEqual("JL_FW\0\0\0\0\0\0\0\0\0\0\0"u8)) return ReadJieli(file, false);
            if (tail.SequenceEqual("JLUFW\0\0\0\0\0\0\0\0\0\0\0"u8)) return ReadJieli(file, true);
        }
        return null;
    }

    private static VendorIdentity ReadBluetrum(byte[] file)
    {
        if (file.Length < 512 || U32(file, 4) != file.Length - 8
            || !file.AsSpan(8, 4).SequenceEqual(new byte[] { 0xC9, 0xCE, 0xC6, 0xCF })
            || !file.AsSpan(56, 4).SequenceEqual(new byte[] { 0xC4, 0xC1, 0xD4, 0xC1 }))
            throw new InvalidDataException("蓝讯容器头或文件长度不一致。");
        uint infoLength = U32(file, 12), dataHeaderLength = U32(file, 60);
        if (infoLength < 20 || infoLength > 256 || dataHeaderLength < 16 || dataHeaderLength > 256
            || 64L + dataHeaderLength > file.Length || !file.AsSpan(16, 4).SequenceEqual("ZOOM"u8))
            throw new InvalidDataException("蓝讯 INFO / DATA 结构无效。");
        string code = $"{U32(file, 28):X8}_{U32(file, 24):X8}";
        var metadata = VendorMetadata.ReadBluetrum(file);
        return new("蓝讯", file[0] == (byte)'P' ? "烧录固件" : "升级固件", code, metadata.Chip, null,
            "已读取蓝讯 INFO 官方校验码；芯片为包内标识对应系列，蓝牙名为固件默认配置。未实现所有数据块的完整性重算。", metadata.Name);
    }

    private static VendorIdentity ReadJieli(byte[] file, bool upgrade)
    {
        ushort key = ushort.MaxValue;
        if (!upgrade)
        {
            var keyRecord = file.AsSpan(file.Length - 64, 34);
            if (Crc16(keyRecord[..32]) != U16(keyRecord, 32)) throw new InvalidDataException("杰理芯片配置记录 CRC 不匹配。");
            key = DecodeKey(keyRecord[..32]);
        }
        byte[] header = Decode(file.AsSpan(0, 64), key);
        if (Crc16(header.AsSpan(2)) != U16(header, 0) || U32(header, 4) != file.Length)
            throw new InvalidDataException("杰理包头 CRC 或文件长度不匹配。");
        int count = U16(header, 8);
        if (count is < 1 or > 1024 || 64L + count * 80L > file.Length) throw new InvalidDataException("杰理目录数量越界。");
        if (Crc16(file.AsSpan(64, count * 80)) != U16(header, 2)) throw new InvalidDataException("杰理目录 CRC 不匹配。");
        string chip = AsciiName(header.AsSpan(16, 48));
        if (!Regex.IsMatch(chip, @"\A[A-Z][A-Z0-9_-]{2,31}\z")) throw new InvalidDataException("杰理芯片字段格式无效。");
        var entries = new List<Entry>();
        for (int i = 0; i < count; i++)
        {
            byte[] raw = Decode(file.AsSpan(64 + i * 80, 80), key);
            uint offset = U32(raw, 8), size = U32(raw, 12), allocated = U32(raw, 16);
            if (offset < 64 + count * 80 || offset > file.Length || size > allocated || (long)offset + allocated > file.Length)
                throw new InvalidDataException("杰理目录数据范围越界。");
            string name = AsciiName(raw.AsSpan(64, 16));
            if (string.IsNullOrWhiteSpace(name) || U16(raw, 2) != i) throw new InvalidDataException("杰理目录名称或顺序无效。");
            var entry = new Entry(U16(raw, 0), U16(raw, 2), U16(raw, 4), (int)offset, (int)size, (int)allocated, name);
            int crcLength = upgrade ? entry.Size : entry.Allocated;
            bool verifiableRawSegment = !upgrade || entry.Type is 0 or 32 or 100 or 255;
            if (verifiableRawSegment && crcLength > 0 && Crc16(file.AsSpan(entry.Offset, crcLength)) != entry.Crc)
                throw new InvalidDataException($"杰理数据段 {name} CRC 不匹配。");
            entries.Add(entry);
        }
        var tailEntry = entries.SingleOrDefault(e => e.Type == 255 && e.Name == "tail.bin");
        if (tailEntry == null || tailEntry.Offset != file.Length - 64 || tailEntry.Size != 64)
            throw new InvalidDataException("杰理尾部目录与内部标记不一致。");
        var flashes = entries.Where(e => (e.Type is 0 or 32) && e.Name.StartsWith("flash", StringComparison.Ordinal) && e.Name.EndsWith(".bin", StringComparison.Ordinal)).ToArray();
        if (flashes.Length == 0) throw new InvalidDataException("杰理包缺少 Flash 数据段。");
        string code;
        if (!upgrade)
        {
            // Official FW: CRC16/CCITT init=0; reflected CRC32 raw init=26536734, no final xor.
            code = $"{Crc16(file):X4}-{RawCrc32(file, 0x26536734):X8}";
        }
        else
        {
            var codes = flashes.Select(e =>
            {
                if (e.Size < 8) throw new InvalidDataException("杰理 Flash 校验记录缺失。");
                int offset = e.Offset + e.Size - 8;
                if (file[offset + 6] != 255 || file[offset + 7] != 255) throw new InvalidDataException("杰理 Flash 校验记录格式无效。");
                return $"{U16(file, offset + 4):X4}-{U32(file, offset):X8}";
            }).Distinct().ToArray();
            if (codes.Length != 1 || codes[0] == "FFFF-FFFFFFFF") throw new InvalidDataException("杰理多 Flash 段校验码冲突或为空。");
            code = codes[0];
        }
        var names = flashes.Select(e => VendorMetadata.ReadJieli(file.AsSpan(e.Offset, e.Size))).Distinct().ToArray();
        string bluetoothName = names.Length == 1 ? names[0] : "未识别";
        return new("杰理", upgrade ? "升级固件" : "烧录固件", code, chip, upgrade ? null : true,
            upgrade ? "杰理包头、目录、Flash/OTA/尾部原始数据段CRC已验证；部分附加段经过独立编码，未验证其明文CRC。版本码读取自受CRC保护的Flash记录。"
            : "杰理包头、目录及全部目录分配数据段CRC已验证，整文件按官方算法计算版本码。", bluetoothName);
    }

    internal static ushort DecodeKey(ReadOnlySpan<byte> data)
    {
        int sum = 0;
        for (int i = 0; i < 16; i++) sum += data[i];
        sum &= 255;
        sum = sum >= 224 ? 170 : sum <= 16 ? 85 : sum;
        ushort key = 0;
        for (int i = 0; i < 16; i++) if ((data[16 + i] ^ data[15 - i]) < sum) key |= (ushort)(1 << i);
        return key;
    }

    internal static byte[] Decode(ReadOnlySpan<byte> data, ushort key)
    {
        byte[] decoded = data.ToArray();
        for (int i = 0; i < decoded.Length; i++)
        {
            decoded[i] ^= (byte)key;
            key = (ushort)((key << 1) ^ ((key & 0x8000) != 0 ? 0x1021 : 0));
        }
        return decoded;
    }

    internal static ushort Crc16(ReadOnlySpan<byte> data)
    {
        ushort crc = 0;
        foreach (byte b in data)
        {
            crc ^= (ushort)(b << 8);
            for (int bit = 0; bit < 8; bit++) crc = (ushort)((crc << 1) ^ ((crc & 0x8000) != 0 ? 0x1021 : 0));
        }
        return crc;
    }

    private static uint RawCrc32(ReadOnlySpan<byte> data, uint crc)
    {
        foreach (byte b in data)
        {
            crc ^= b;
            for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xEDB88320u : 0);
        }
        return crc;
    }

    private static string AsciiName(ReadOnlySpan<byte> bytes)
    {
        int end = bytes.IndexOf((byte)0);
        if (end >= 0) bytes = bytes[..end];
        return Encoding.ASCII.GetString(bytes);
    }
    private static ushort U16(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
    private static uint U32(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
}
