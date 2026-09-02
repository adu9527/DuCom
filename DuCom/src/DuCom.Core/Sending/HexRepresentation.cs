using System.Globalization;
using System.Text;

namespace DuCom.Core.Sending;

/// <summary>
/// Pure text/HEX representations for export operations. Format: uppercase byte pairs
/// separated by single spaces ("AB CD EF"), matching the HEX display/send formats.
/// </summary>
public static class HexRepresentation
{
    public static string ToHexText(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return string.Empty;
        }

        StringBuilder builder = new(data.Length * 3 - 1);
        foreach (byte value in data)
        {
            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            builder.Append(value.ToString("X2", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    /// <summary>
    /// Parses a HEX text representation back to bytes. Odd digits, invalid characters, and
    /// separators other than spaces make the input invalid (returns false with no output).
    /// </summary>
    public static bool TryParseHexText(ReadOnlySpan<char> text, out byte[] bytes)
    {
        bytes = [];
        if (text.IsEmpty)
        {
            return true;
        }

        List<byte> result = [];
        Span<char> digits = stackalloc char[2];
        int digitCount = 0;
        foreach (char character in text)
        {
            if (character == ' ')
            {
                if (digitCount == 1)
                {
                    return false;
                }

                digitCount = 0;
                continue;
            }

            if (character is >= '0' and <= '9' or >= 'A' and <= 'F' or >= 'a' and <= 'f')
            {
                if (digitCount == 2)
                {
                    return false;
                }

                digits[digitCount++] = character;
                if (digitCount == 2)
                {
                    result.Add(byte.Parse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                    digitCount = 0;
                }

                continue;
            }

            return false;
        }

        if (digitCount != 0)
        {
            return false;
        }

        bytes = [.. result];
        return true;
    }
}
