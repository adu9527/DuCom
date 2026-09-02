using System.Collections.Concurrent;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace DuCom.Converters;

public sealed class RgbToBrushConverter : IMultiValueConverter
{
    private static readonly ConcurrentDictionary<int, SolidColorBrush> BrushCache = new();

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length == 3 &&
            values[0] is byte r &&
            values[1] is byte g &&
            values[2] is byte b)
        {
            return GetBrush(r, g, b);
        }

        return DependencyProperty.UnsetValue;
    }

    public static SolidColorBrush GetBrush(byte r, byte g, byte b)
    {
        int key = (r << 16) | (g << 8) | b;
        return BrushCache.GetOrAdd(key, static (_, rgb) =>
        {
            SolidColorBrush brush = new(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
            brush.Freeze();
            return brush;
        }, key);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
