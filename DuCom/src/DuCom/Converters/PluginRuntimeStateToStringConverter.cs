using System.Globalization;
using System.Windows;
using System.Windows.Data;
using DuCom.PluginHost.Core;

namespace DuCom.Converters;

public sealed class PluginRuntimeStateToStringConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not PluginRuntimeState state)
        {
            return DependencyProperty.UnsetValue;
        }

        return Application.Current.TryFindResource($"Plugins.State.{state}") ?? state.ToString();
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
