using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using DuCom.Core.Parsing;

namespace DuCom.Converters;

public sealed class RuleKindToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is HighlightFilterRule rule && rule.HasForeground)
        {
            return new SolidColorBrush(Color.FromRgb(rule.ForegroundR!.Value, rule.ForegroundG!.Value, rule.ForegroundB!.Value));
        }

        return Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
