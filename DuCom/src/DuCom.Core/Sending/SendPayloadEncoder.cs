using System.Globalization;
using System.Text;

namespace DuCom.Core.Sending;

public enum SendMode
{
    Str,
    Hex,
}

public enum NewlinePolicy
{
    None,
    Cr,
    Lf,
    CrLf,
}

public static class SendPayloadEncoder
{
    public static byte[] EncodeString(string text, Encoding encoding, NewlinePolicy newline)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(encoding);
        return AppendNewline(encoding.GetBytes(text), newline);
    }

    public static byte[] EncodeHex(string text, NewlinePolicy newline)
    {
        ArgumentNullException.ThrowIfNull(text);
        string compact = string.Concat(text.Where(character => !char.IsWhiteSpace(character)));
        if (compact.Length % 2 != 0)
        {
            throw new FormatException("HEX input must contain complete byte pairs.");
        }

        byte[] bytes = new byte[compact.Length / 2];
        for (int index = 0; index < bytes.Length; index++)
        {
            if (!byte.TryParse(compact.AsSpan(index * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bytes[index]))
            {
                throw new FormatException($"Invalid HEX byte at position {index * 2}.");
            }
        }

        return AppendNewline(bytes, newline);
    }

    private static byte[] AppendNewline(byte[] payload, NewlinePolicy newline)
    {
        ReadOnlySpan<byte> suffix = newline switch
        {
            NewlinePolicy.None => [],
            NewlinePolicy.Cr => "\r"u8,
            NewlinePolicy.Lf => "\n"u8,
            NewlinePolicy.CrLf => "\r\n"u8,
            _ => throw new ArgumentOutOfRangeException(nameof(newline), newline, null),
        };

        if (suffix.IsEmpty)
        {
            return payload;
        }

        byte[] result = new byte[payload.Length + suffix.Length];
        payload.CopyTo(result, 0);
        suffix.CopyTo(result.AsSpan(payload.Length));
        return result;
    }
}
