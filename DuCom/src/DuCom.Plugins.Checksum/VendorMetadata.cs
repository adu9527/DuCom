using System.Buffers.Binary;
using System.Text;

namespace DuCom.Plugins.Checksum;

internal static class VendorMetadata
{
    private const string Unknown = "未识别";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static (string Chip, string Name) ReadBluetrum(byte[] file)
    {
        string chip = file.AsSpan(16, 8).SequenceEqual("ZOOM\x01\0\0\0"u8) ? "AB571X（系列）" : Unknown;
        var names = new HashSet<string>();
        for (int offset = 512; offset <= file.Length - 512; offset += 512)
        {
            var block = file.AsSpan(offset, 512);
            if (file[0] == (byte)'U') ReadBlock(block);
            else
            {
                foreach (byte flags in new byte[] { 1, 0x41 })
                {
                    byte[] expected = AbDecode(new byte[] { 0x5A, flags, 0, (byte)(256 - 0x5A - flags) }, 0x474D564C);
                    uint stream = U32(block, 0) ^ U32(expected, 0);
                    uint low = stream & 0xFFFFFF;
                    uint key = low | ((stream ^ U32(AbDecode(new byte[4], low), 0)) & 0xFF000000);
                    ReadBlock(AbDecode(block, key));
                }
            }
        }
        return (chip, names.Count == 1 ? names.Single() : Unknown);

        void ReadBlock(ReadOnlySpan<byte> block)
        {
            if (block.Length < 341) return;
            byte[] header = AbDecode(block[..64], 0x474D564C);
            if (header[0] != 0x5A || ((header[0] + header[1] + header[2] + header[3]) & 255) != 0
                || !header.AsSpan(4, 8).SequenceEqual(file.AsSpan(16, 8))
                || Crc16(header.AsSpan(0, 62), ushort.MaxValue) != U16(header, 62)
                || !block.Slice(256, 4).SequenceEqual("INFO"u8)
                || U16(block, 260) != 1 || U16(block, 262) != 345) return;
            string name = Name(block.Slice(309, 32));
            if (name != Unknown) names.Add(name);
        }
    }

    internal static string ReadJieli(ReadOnlySpan<byte> flash)
    {
        if (flash.Length < 192) return Unknown;
        int appOffset = -1, configOffset = -1, configSize = 0;
        bool last = false;
        for (int i = 0; i < 128 && 32 + i * 32 <= flash.Length - 32; i++)
        {
            byte[] entry = VendorFormats.Decode(flash.Slice(32 + i * 32, 32), ushort.MaxValue);
            if (!ValidEntry(entry)) return Unknown;
            string name = Ascii(entry.AsSpan(16));
            if (name == "app_dir_head" && entry[12] == 0x81) appOffset = Offset(entry, 4);
            if (name == "isd_config.ini") { configOffset = Offset(entry, 4); configSize = Offset(entry, 8); }
            if (U16(entry, 14) != 0) { last = true; break; }
        }
        if (!last || appOffset < 0 || appOffset % 32 != 0 || appOffset > flash.Length - 32
            || configOffset < 0 || configSize < 34 || configOffset > flash.Length - configSize) return Unknown;
        var config = flash.Slice(configOffset, configSize);
        if (VendorFormats.Crc16(config[..32]) != U16(config, 32)) return Unknown;
        ushort key = VendorFormats.DecodeKey(config[..32]);
        byte[] app = flash[appOffset..].ToArray();
        for (int offset = 0; offset < app.Length; offset += 32)
        {
            byte[] decoded = VendorFormats.Decode(app.AsSpan(offset, Math.Min(32, app.Length - offset)), (ushort)(key ^ (offset >> 2)));
            decoded.CopyTo(app, offset);
        }
        int position = 0;
        for (int i = 0; i < 1024 && position <= app.Length - 32; i++)
        {
            var entry = app.AsSpan(position, 32);
            if (!ValidEntry(entry)) return Unknown;
            int size = Offset(entry, 8);
            if (size < 32 || size > app.Length - position) return Unknown;
            if (i == 0 && (Ascii(entry[16..]) != "app_area_head" || entry[12] != 0x83)) return Unknown;
            if (Ascii(entry[16..]) == "cfg_tool.bin" && entry[12] == 0x82)
            {
                var data = app.AsSpan(position + 32, size - 32);
                if (VendorFormats.Crc16(data) != U16(entry, 2)) return Unknown;
                return ReadJieliConfig(data);
            }
            if (U16(entry, 14) != 0) break;
            position += size;
        }
        return Unknown;
    }

    private static string ReadJieliConfig(ReadOnlySpan<byte> data)
    {
        if (data.Length < 8 || U32(data, 0) != data.Length - 4) return Unknown;
        string result = Unknown;
        for (int position = 4; position < data.Length;)
        {
            if (position > data.Length - 4) return Unknown;
            int bits = data[position + 1] | data[position + 2] << 8 | data[position + 3] << 16;
            int id = bits & 4095, size = bits >> 12;
            position += 4;
            if (size == 0 || size > data.Length - position) return Unknown;
            if (id == 101)
            {
                if (result != Unknown) return Unknown;
                var value = data.Slice(position, size);
                if (size == 32) result = Name(value);
                else if (size % 33 == 0)
                {
                    for (int i = 0; i < size; i += 33)
                        if (value[i] == 1) { result = Name(value.Slice(i + 1, 32)); break; }
                }
            }
            position += size;
        }
        return result;
    }

    private static string Name(ReadOnlySpan<byte> bytes)
    {
        int end = bytes.IndexOf((byte)0);
        if (end <= 0 || bytes[(end + 1)..].ContainsAnyExcept((byte)0)) return Unknown;
        try
        {
            string name = StrictUtf8.GetString(bytes[..end]);
            return string.IsNullOrWhiteSpace(name) || name.Any(char.IsControl) ? Unknown : name;
        }
        catch (DecoderFallbackException) { return Unknown; }
    }

    private static byte[] AbDecode(ReadOnlySpan<byte> data, uint key)
    {
        byte[] result = data.ToArray();
        for (int i = 0; i < result.Length; i++)
        {
            result[i] ^= (byte)key;
            for (int bit = 0; bit < 8; bit++) key = (key >> 1) ^ ((key & 1) != 0 ? 0xA3000000u : 0);
        }
        return result;
    }

    private static ushort Crc16(ReadOnlySpan<byte> bytes, ushort crc)
    {
        foreach (byte value in bytes)
        {
            crc ^= (ushort)(value << 8);
            for (int bit = 0; bit < 8; bit++) crc = (ushort)((crc << 1) ^ ((crc & 0x8000) != 0 ? 0x1021 : 0));
        }
        return crc;
    }

    private static bool ValidEntry(ReadOnlySpan<byte> entry) => VendorFormats.Crc16(entry[2..]) == U16(entry, 0);
    private static string Ascii(ReadOnlySpan<byte> data) { int end = data.IndexOf((byte)0); return Encoding.ASCII.GetString(end < 0 ? data : data[..end]); }
    private static int Offset(ReadOnlySpan<byte> data, int offset) => U32(data, offset) <= int.MaxValue ? (int)U32(data, offset) : -1;
    private static ushort U16(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
    private static uint U32(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
}
