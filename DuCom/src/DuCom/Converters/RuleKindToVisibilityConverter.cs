using System.Globalization;
using System.Windows;
using System.Windows.Data;
using DuCom.Core.Parsing;

namespace DuCom.Converters;

public sealed class RuleKindToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is HighlightFilterRuleKind.Highlight ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
