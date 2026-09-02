using System.Globalization;
using System.Windows;
using System.Windows.Data;
using DuCom.Core.Ports;

namespace DuCom.Converters;

public sealed class LifecycleStateToStringConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not PortLifecycleState state)
        {
            return DependencyProperty.UnsetValue;
        }

        string key = state switch
        {
            PortLifecycleState.Open => "Connection.OpenState",
            PortLifecycleState.Opening => "Connection.OpeningState",
            PortLifecycleState.Closing => "Connection.ClosingState",
            _ => "Connection.ClosedState",
        };

        return Application.Current.TryFindResource(key) ?? state.ToString();
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
