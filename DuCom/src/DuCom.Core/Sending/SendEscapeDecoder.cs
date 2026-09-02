using System.Globalization;
using System.Text;

namespace DuCom.Core.Sending;

public static class SendEscapeDecoder
{
    public static string Decode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        StringBuilder result = new(text.Length);
        for (int index = 0; index < text.Length; index++)
        {
            char current = text[index];
            if (current != '\\' || index + 1 >= text.Length)
            {
                result.Append(current);
                continue;
            }

            char escaped = text[++index];
            switch (escaped)
            {
                case 'r': result.Append('\r'); break;
                case 'n': result.Append('\n'); break;
                case 't': result.Append('\t'); break;
                case '\\': result.Append('\\'); break;
                case 'x' when index + 2 < text.Length &&
                    byte.TryParse(text.AsSpan(index + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte value):
                    result.Append((char)value);
                    index += 2;
                    break;
                default: result.Append('\\').Append(escaped); break;
            }
        }

        return result.ToString();
    }
}
