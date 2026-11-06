using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DuCom.Services;

/// <summary>Rendering defaults without replacing native or WPF-UI control templates.</summary>
internal static class WindowRenderingPolicy
{
    private static bool _initialized;

    internal static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) => Apply((Window)sender)));
    }

    internal static void Apply(Window window)
    {
        Default(Control.FontFamilyProperty, new FontFamily("Segoe UI, Microsoft YaHei UI"));
        Default(Control.FontSizeProperty, 14d);
        Default(Control.FontWeightProperty, FontWeights.Normal);
        Default(FrameworkElement.UseLayoutRoundingProperty, true);
        Default(TextOptions.TextFormattingModeProperty, TextFormattingMode.Display);
        Default(TextOptions.TextRenderingModeProperty, TextRenderingMode.Auto);
        Default(RenderOptions.ClearTypeHintProperty, ClearTypeHint.Enabled);

        void Default(DependencyProperty property, object value)
        {
            if (window.ReadLocalValue(property) == DependencyProperty.UnsetValue)
                window.SetCurrentValue(property, value);
        }
    }
}
