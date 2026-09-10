using System.Windows;
using System.Windows.Threading;
using DuCom.Core.Diagnostics;
using Wpf.Ui.Appearance;
using WindowBackdropType = Wpf.Ui.Controls.WindowBackdropType;

namespace DuCom;

public partial class App : Application, IDisposable
{
    private CompositionRoot? _compositionRoot;
    private Services.Plugins.PluginSystemHost? _pluginSystemHost;

    internal string CurrentLanguage { get; private set; } = "en-US";

    internal string CurrentThemeMode { get; private set; } = "Dark";

    internal bool IsThemeSpecifiedOnCommandLine { get; private set; }

    internal DiagnosticFileLog? DiagnosticLog { get; set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DiagnosticLog?.Information($"WPF startup entered. Arguments={string.Join(' ', e.Args)}");
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        Dictionary<string, string> arguments = ParseArguments(e.Args);
        IsThemeSpecifiedOnCommandLine = arguments.ContainsKey("theme");
        string language = ResolveLanguage(arguments);
        DiagnosticLog?.Information($"Loading language resources. Language={language}");
        LoadLanguageResources(language);

        string persistedThemeMode = LoadPersistedThemeMode();
        CurrentThemeMode = ResolveThemeMode(arguments, persistedThemeMode);
        ApplicationTheme applicationTheme = ResolveTheme(CurrentThemeMode);
        DiagnosticLog?.Information($"Applying application theme. Theme={applicationTheme}");
        ApplicationThemeManager.Changed += OnApplicationThemeChanged;
        ApplicationThemeManager.Apply(
            applicationTheme,
            WindowBackdropType.Mica,
            updateAccent: true);

        _compositionRoot = new CompositionRoot();
        DiagnosticLog?.Information("Creating main window.");
        MainWindow = _compositionRoot.CreateMainWindow();
        MainWindow.Show();
        DiagnosticLog?.Information("Main window shown.");

        InitializePluginSystem();

        if (arguments.ContainsKey("smoke-test"))
        {
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => RunShellSmokeTest(MainWindow));
        }
        else if (arguments.ContainsKey("plugins-smoke"))
        {
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => RunPluginsSmokeTest(MainWindow));
        }
        else if (arguments.ContainsKey("plugin-ui-smoke"))
        {
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => RunPluginUiSmokeTest(MainWindow));
        }
        else if (arguments.ContainsKey("about-smoke"))
        {
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => RunAboutSmokeTest(MainWindow));
        }
        else if (arguments.ContainsKey("rules-smoke"))
        {
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => RunRulesSmokeTest(MainWindow));
        }
        else if (arguments.ContainsKey("tools-smoke"))
        {
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => RunToolsSmokeTest(MainWindow));
        }
        else if (arguments.ContainsKey("close-smoke"))
        {
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, MainWindow.Close);
        }
        else if (arguments.ContainsKey("split-smoke"))
        {
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => RunSplitSmokeTest(MainWindow));
        }
        else if (arguments.ContainsKey("settings-smoke"))
        {
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => RunSettingsSmokeTest(MainWindow));
        }
        else if (arguments.ContainsKey("commands-smoke"))
        {
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => RunCommandsSmokeTest(MainWindow));
        }
        else if (arguments.ContainsKey("editor-smoke"))
        {
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => RunEditorSmokeTest(MainWindow));
        }
        else
        {
            Dispatcher.BeginInvoke(
                DispatcherPriority.ApplicationIdle,
                () => _ = Services.Updates.UpdateFlow.RunAutomaticCheckAsync());
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ApplicationThemeManager.Changed -= OnApplicationThemeChanged;
        Services.SystemPowerService.SetPreventSleep(false);
        DiagnosticLog?.Information($"WPF application exiting. ExitCode={e.ApplicationExitCode}");
        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
        Dispose();
        base.OnExit(e);
    }

    public void Dispose()
    {
        if (_pluginSystemHost is not null)
        {
            try
            {
                Task.Run(async () => await _pluginSystemHost.DisposeAsync()).GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                DiagnosticLog?.Warning("Plugin system shutdown reported a failure.", exception);
            }

            _pluginSystemHost = null;
        }

        if (_compositionRoot is not null)
        {
            Task.Run(async () => await _compositionRoot.DisposeAsync()).GetAwaiter().GetResult();
            _compositionRoot = null;
        }

        GC.SuppressFinalize(this);
    }

}
