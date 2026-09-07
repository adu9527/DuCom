using System.Globalization;
using System.Windows.Data;

namespace DuCom.Converters;

public sealed class PercentageValueConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double number ? number * 100d : 0d;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double number ? number / 100d : 0d;
}
