using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DuCom.Converters;

/// <summary>Returns true when the bound value equals the ConverterParameter.</summary>
public sealed class EqualityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Equals(value?.ToString(), parameter?.ToString()) ? BooleanBoxes.True : BooleanBoxes.False;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? parameter ?? DependencyProperty.UnsetValue : DependencyProperty.UnsetValue;

    private static class BooleanBoxes
    {
        public static readonly object True = true;
        public static readonly object False = false;
    }
}
