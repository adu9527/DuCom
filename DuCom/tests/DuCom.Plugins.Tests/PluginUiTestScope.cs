using System.Windows;
using System.Windows.Media;

namespace DuCom.Plugins.Tests;

/// <summary>Test-only host defaults plus dispatcher-local window resources; never reattaches another STA's dictionaries.</summary>
internal sealed class PluginUiTestScope : IDisposable
{
    private readonly List<Window> _windows = [];

    internal PluginUiTestScope()
    {
        Application app = Application.Current ?? throw new InvalidOperationException("Run UI tests through WpfTestDispatcher.");
        app.Dispatcher.VerifyAccess();
        // Constructors resolve these before Track can supply window-local resources.
        // Keep only immutable test-host defaults globally; never Clear/restore app resources
        // while other test dispatchers may still own resource expressions or live windows.
        app.Resources["LogPackage.Cancel"] = "Close";
        app.Resources["Brush.PanelRaised"] = Brushes.Transparent;
        app.Resources["Brush.PanelBorder"] = Brushes.Transparent;
        app.Resources["Brush.Danger"] = Brushes.Red;
        Style toggle = new(typeof(System.Windows.Controls.Primitives.ToggleButton));
        toggle.Seal();
        app.Resources["Style.FloatToolbarToggle"] = toggle;
    }

    internal void Track(Window window)
    {
        window.Dispatcher.VerifyAccess();
        _windows.Add(window);
        // Insert before the window's authored dictionaries: retain PluginUiStyles and
        // compact production styles as the authority while supplying fresh WPF defaults.
        window.Resources.MergedDictionaries.Insert(0, new ResourceDictionary
        {
            Source = new Uri("/DuCom;component/Resources/DesignTokens.Colors.Dark.xaml", UriKind.Relative),
        });
        window.Resources.MergedDictionaries.Insert(0, new Wpf.Ui.Markup.ControlsDictionary());
        window.Resources.MergedDictionaries.Insert(0, new Wpf.Ui.Markup.ThemesDictionary { Theme = Wpf.Ui.Appearance.ApplicationTheme.Dark });
    }

    public void Dispose()
    {
        // Application.Windows belongs to the original STA; close only our tracked windows.
        foreach (Window window in _windows.AsEnumerable().Reverse())
        {
            window.Dispatcher.VerifyAccess();
            if (!window.IsVisible) continue;
            System.Reflection.MethodInfo? closeFromHost = window.GetType().GetMethod("CloseFromHost",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (closeFromHost is not null) closeFromHost.Invoke(window, null);
            else window.Close();
        }
    }
}
