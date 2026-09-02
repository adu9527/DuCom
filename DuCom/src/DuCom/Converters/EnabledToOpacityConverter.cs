using System.Globalization;
using System.Windows.Data;

namespace DuCom.Converters;

public sealed class EnabledToOpacityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is true ? 1.0 : 0.5;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
