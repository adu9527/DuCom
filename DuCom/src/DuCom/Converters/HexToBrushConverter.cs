using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace DuCom.Converters;

public sealed class HexToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string hex && TryParseHex(hex, out byte r, out byte g, out byte b))
        {
            return new SolidColorBrush(Color.FromRgb(r, g, b));
        }

        return Brushes.Transparent;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static bool TryParseHex(string hex, out byte r, out byte g, out byte b)
    {
        r = 0;
        g = 0;
        b = 0;

        if (string.IsNullOrWhiteSpace(hex))
        {
            return false;
        }

        ReadOnlySpan<char> span = hex.AsSpan().Trim();
        if (span.Length > 0 && span[0] == '#')
        {
            span = span[1..];
        }

        if (span.Length == 3)
        {
            if (!byte.TryParse(span[..1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte rValue) ||
                !byte.TryParse(span[1..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte gValue) ||
                !byte.TryParse(span[2..3], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte bValue))
            {
                return false;
            }

            r = (byte)(rValue * 16 + rValue);
            g = (byte)(gValue * 16 + gValue);
            b = (byte)(bValue * 16 + bValue);
            return true;
        }

        return span.Length == 6 &&
            byte.TryParse(span[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out r) &&
            byte.TryParse(span[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out g) &&
            byte.TryParse(span[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b);
    }
}
