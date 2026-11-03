using System.Globalization;
using System.Text;

namespace DuCom.Plugins.Crc;

public sealed record CrcModel(string Name, int Width, uint Poly, uint Init, uint XorOut, bool RefIn, bool RefOut)
{
    public string PolyHex => Format(Poly);
    public string InitHex => Format(Init);
    public string XorHex => Format(XorOut);
    public string Formula => "x^" + Width + " + " + string.Join(" + ", Enumerable.Range(0, Width).Reverse()
        .Where(bit => (Poly & (1u << bit)) != 0).Select(bit => bit == 0 ? "1" : bit == 1 ? "x" : "x^" + bit));
    private string Format(uint value) => value.ToString("X" + Math.Max(2, (Width + 3) / 4), CultureInfo.InvariantCulture);
}

public static class CrcCalculator
{
    public static IReadOnlyList<CrcModel> Models { get; } = Array.AsReadOnly<CrcModel>([
        new("CRC-4/ITU", 4, 0x03, 0, 0, true, true),
        new("CRC-5/EPC", 5, 0x09, 0x09, 0, false, false),
        new("CRC-5/ITU", 5, 0x15, 0, 0, true, true),
        new("CRC-5/USB", 5, 0x05, 0x1F, 0x1F, true, true),
        new("CRC-6/ITU", 6, 0x03, 0, 0, true, true),
        new("CRC-7/MMC", 7, 0x09, 0, 0, false, false),
        new("CRC-8", 8, 0x07, 0, 0, false, false),
        new("CRC-8/ITU", 8, 0x07, 0, 0x55, false, false),
        new("CRC-8/ROHC", 8, 0x07, 0xFF, 0, true, true),
        new("CRC-8/MAXIM", 8, 0x31, 0, 0, true, true),
        new("CRC-16/IBM", 16, 0x8005, 0, 0, true, true),
        new("CRC-16/MAXIM", 16, 0x8005, 0, 0xFFFF, true, true),
        new("CRC-16/USB", 16, 0x8005, 0xFFFF, 0xFFFF, true, true),
        new("CRC-16/MODBUS", 16, 0x8005, 0xFFFF, 0, true, true),
        new("CRC-16/CCITT", 16, 0x1021, 0, 0, true, true),
        new("CRC-16/CCITT-FALSE", 16, 0x1021, 0xFFFF, 0, false, false),
        new("CRC-16/X25", 16, 0x1021, 0xFFFF, 0xFFFF, true, true),
        new("CRC-16/XMODEM", 16, 0x1021, 0, 0, false, false),
        new("CRC-16/DNP", 16, 0x3D65, 0, 0xFFFF, true, true),
        new("CRC-32", 32, 0x04C11DB7, uint.MaxValue, uint.MaxValue, true, true),
        new("CRC-32/MPEG-2", 32, 0x04C11DB7, uint.MaxValue, 0, false, false),
    ]);

    public static uint Mask(int width) => width is >= 1 and <= 32 ? uint.MaxValue >> (32 - width)
        : throw new ArgumentOutOfRangeException(nameof(width), "WIDTH 必须在 1–32 之间。");

    public static CrcModel ParseModel(IReadOnlyDictionary<string, string> values)
    {
        string Field(string key) => values.TryGetValue(key, out string? value) ? value : "";
        if (!int.TryParse(Field("width"), out int width) || width is < 1 or > 32)
            throw new FormatException("WIDTH 必须在 1–32 之间。");
        uint Parse(string key, string label)
        {
            string text = Field(key).Trim();
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text[2..];
            if (!uint.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint value) || value > Mask(width))
                throw new FormatException(label + " 必须是宽度范围内的十六进制数。");
            return value;
        }
        uint poly = Parse("poly", "POLY");
        if (poly == 0) throw new FormatException("POLY 不能为 0。");
        return new CrcModel("自定义", width, poly, Parse("init", "INIT"), Parse("xor", "XOROUT"),
            bool.TryParse(Field("refin"), out bool refin) && refin, bool.TryParse(Field("refout"), out bool refout) && refout);
    }

    public static byte[] ParseInput(string text, bool ascii)
    {
        if (ascii)
        {
            if (text.Any(character => character > 127)) throw new FormatException("ASCII 仅支持 0–127 字符；其他数据请用 Hex 输入。");
            return Encoding.ASCII.GetBytes(text);
        }
        StringBuilder hex = new();
        for (int index = 0; index < text.Length; index++)
        {
            char character = text[index];
            if (char.IsWhiteSpace(character) || character is ',' or ';' or ':' or '-') continue;
            if (character == '0' && index + 1 < text.Length && text[index + 1] is 'x' or 'X') { index++; continue; }
            if (!Uri.IsHexDigit(character)) throw new FormatException($"Hex 含无效字符“{character}”，请使用成对的十六进制字节。");
            hex.Append(character);
        }
        if (hex.Length % 2 != 0) throw new FormatException("Hex 位数必须为偶数，例如 31 32 33 34。");
        return Convert.FromHexString(hex.ToString());
    }

    public static uint Append(uint register, ReadOnlySpan<byte> data, CrcModel model)
    {
        uint mask = Mask(model.Width), top = 1u << (model.Width - 1);
        foreach (byte value in data)
        {
            for (int bit = 0; bit < 8; bit++)
            {
                bool input = (value & (1 << (model.RefIn ? bit : 7 - bit))) != 0;
                bool feedback = (register & top) != 0;
                register = (register << 1) & mask;
                if (feedback != input) register ^= model.Poly;
            }
        }
        return register & mask;
    }

    public static uint Finish(uint register, CrcModel model)
    {
        if (model.RefOut)
        {
            uint reflected = 0;
            for (int bit = 0; bit < model.Width; bit++) { reflected = (reflected << 1) | (register & 1); register >>= 1; }
            register = reflected;
        }
        return (register ^ model.XorOut) & Mask(model.Width);
    }

    public static uint Compute(ReadOnlySpan<byte> data, CrcModel model) => Finish(Append(model.Init, data, model), model);
    public static string Hex(uint value, int width) => value.ToString("X" + ((width + 3) / 4), CultureInfo.InvariantCulture);
    public static string Binary(uint value, int width) => Convert.ToString(value, 2).PadLeft(width, '0');
}
