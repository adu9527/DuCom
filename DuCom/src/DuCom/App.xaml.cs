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
    private Task? _shutdownTask;

    internal string CurrentLanguage { get; private set; } = "en-US";

    internal string CurrentThemeMode { get; private set; } = "Dark";

    internal bool IsThemeSpecifiedOnCommandLine { get; private set; }

    internal DiagnosticFileLog? DiagnosticLog { get; set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        Services.WindowRenderingPolicy.Initialize();
        base.OnStartup(e);
        DiagnosticLog?.Information($"WPF startup entered. Arguments={string.Join(' ', e.Args)}");
        Services.TaskbarIconService.Initialize(Services.Updates.BuildDistribution.Current != Services.Updates.DistributionKind.Installed);
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

        // Stable DIP typography; PerMonitorV2 handles each window's monitor scale.
        // Apply before window construction so templates share the same type scale.
        Services.DensityService.Apply(Resources);
        DiagnosticLog?.Information(
            $"Density applied. Mode={Services.DensityService.Mode}; "
            + $"Screen={Services.DensityService.PhysicalWidth}x{Services.DensityService.PhysicalHeight}; "
            + $"DpiScale={Services.DensityService.DpiScale:F2}; Factor={Services.DensityService.Factor:F2}");

        _compositionRoot = new CompositionRoot();
        DiagnosticLog?.Information("Creating main window.");
        MainWindow = _compositionRoot.CreateMainWindow();
        Services.TaskbarIconService.Attach(MainWindow);
        MainWindow.Show();
        DiagnosticLog?.Information("Main window shown.");

        InitializePluginSystem();

        if (arguments.TryGetValue("ui-review-smoke", out string? reviewOutput))
        {
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => RunUiReviewSmokeTest(MainWindow, reviewOutput));
        }
        else if (arguments.ContainsKey("smoke-test"))
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
        Program.BeginShutdown();
        ApplicationThemeManager.Changed -= OnApplicationThemeChanged;
        Services.SystemPowerService.SetPreventSleep(false);
        DiagnosticLog?.Information($"WPF application exiting. ExitCode={e.ApplicationExitCode}");
        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
        Dispose();
        base.OnExit(e);
    }

    internal Task PrepareShutdownAsync() => _shutdownTask ??= PrepareShutdownCoreAsync();

    private async Task PrepareShutdownCoreAsync()
    {
        Program.BeginShutdown();
        // Complete asynchronous teardown before closing the main window: the
        // responsiveness monitor and plugin callbacks may still need its dispatcher.
        if (_pluginSystemHost is not null)
        {
            Services.Plugins.PluginSystemHost host = _pluginSystemHost;
            _pluginSystemHost = null;
            try { await host.DisposeAsync(); }
            catch (Exception exception) { DiagnosticLog?.Warning("Plugin system shutdown reported a failure.", exception); }
        }

        if (_compositionRoot is not null)
        {
            CompositionRoot root = _compositionRoot;
            _compositionRoot = null;
            try
            {
                // The view model closes WPF tool windows and stops dispatcher timers.
                // Run that part on the UI thread before the root's background teardown.
                await root.MainViewModel.DisposeAsync();
                await root.DisposeAsync();
            }
            catch (Exception exception) { DiagnosticLog?.Warning("Application services shutdown reported a failure.", exception); }
        }
    }

    public void Dispose()
    {
        Program.BeginShutdown();
        // OnExit runs this on the UI thread. The graceful path (MainWindow close) has
        // already completed PrepareShutdownAsync, so these usually find null fields.
        // Direct Shutdown() callers (update restart) may still get here mid-teardown:
        // bound the synchronous wait so the UI thread can never block unboundedly.
        // If the bound expires we simply exit; every worker sits in a
        // KILL_ON_JOB_CLOSE job, so the OS terminates children when the host dies.
        if (_pluginSystemHost is not null)
        {
            try
            {
                WaitForDisposal(Task.Run(() => _pluginSystemHost.DisposeAsync().AsTask()), "plugin system");
            }
            catch (Exception exception)
            {
                DiagnosticLog?.Warning("Plugin system shutdown reported a failure.", exception);
            }

            _pluginSystemHost = null;
        }

        if (_compositionRoot is not null)
        {
            try
            {
                WaitForDisposal(Task.Run(() => _compositionRoot.DisposeAsync().AsTask()), "application services");
            }
            catch (Exception exception)
            {
                DiagnosticLog?.Warning("Application services shutdown reported a failure.", exception);
            }
            _compositionRoot = null;
        }

        GC.SuppressFinalize(this);
    }

    private static void WaitForDisposal(Task disposal, string name)
    {
        if (disposal.IsCompleted || disposal.Wait(TimeSpan.FromSeconds(10)))
        {
            return;
        }

        Program.DiagnosticLog?.Warning(
            $"{name} disposal did not complete within 10s during exit; exiting anyway. Worker jobs terminate with the host.");
    }

}
