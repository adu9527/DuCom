using System.Globalization;
using System.Windows.Data;

namespace DuCom.Converters;

/// <summary>Returns true when all bound values are equal as strings (case-insensitive).</summary>
public sealed class EqualityMultiConverter : IMultiValueConverter
{
    public object Convert(object?[]? values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values is null || values.Length < 2)
        {
            return false;
        }

        string? first = values[0]?.ToString();
        for (int index = 1; index < values.Length; index++)
        {
            if (!string.Equals(first, values[index]?.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        [Binding.DoNothing];
}
