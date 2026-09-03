using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using DuCom.Core.Diagnostics;
using DuCom.Core.Parsing;
using DuCom.Services.Shortcuts;
using DuCom.ViewModels;
using Wpf.Ui.Appearance;
using WindowBackdropType = Wpf.Ui.Controls.WindowBackdropType;

namespace DuCom;

public partial class App : Application, IDisposable
{
    private CompositionRoot? _compositionRoot;

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

        if (arguments.ContainsKey("smoke-test"))
        {
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => RunShellSmokeTest(MainWindow));
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
        else if (arguments.ContainsKey("editor-smoke"))
        {
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => RunEditorSmokeTest(MainWindow));
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
        if (_compositionRoot is not null)
        {
            _compositionRoot.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _compositionRoot = null;
        }

        GC.SuppressFinalize(this);
    }

    internal void ApplyLanguage(string language)
    {
        string normalized = string.Equals(language, "zh-CN", StringComparison.OrdinalIgnoreCase) ? "zh-CN" : "en-US";
        CurrentLanguage = normalized;
        ResourceDictionary? existing = Resources.MergedDictionaries.FirstOrDefault(
            dictionary => dictionary.Source?.OriginalString.StartsWith("Resources/Languages/", StringComparison.OrdinalIgnoreCase) == true);
        if (existing is not null)
        {
            Resources.MergedDictionaries.Remove(existing);
        }

        CultureInfo culture = CultureInfo.GetCultureInfo(normalized);
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri($"Resources/Languages/{normalized}.xaml", UriKind.Relative),
        });
    }

    private void LoadLanguageResources(string language) => ApplyLanguage(language);

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Trace.TraceError(e.Exception.ToString());
        DiagnosticLog?.Error("Unhandled Dispatcher exception.", e.Exception);
        e.Handled = true;
        ThemedMessageDialog.Show(
            MainWindow,
            (string)FindResource("Error.UnhandledMessage"),
            (string)FindResource("Error.UnhandledTitle"),
            ThemedMessageDialogKind.Error);
        Shutdown(-1);
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        Trace.TraceError(e.ExceptionObject?.ToString());
        DiagnosticLog?.Error(
            $"Unhandled AppDomain exception. IsTerminating={e.IsTerminating}",
            e.ExceptionObject as Exception);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Trace.TraceError(e.Exception.ToString());
        DiagnosticLog?.Error("Unobserved Task exception.", e.Exception);
        e.SetObserved();
    }

    private static Dictionary<string, string> ParseArguments(string[] values)
    {
        Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < values.Length; index++)
        {
            if (values[index].StartsWith("--", StringComparison.Ordinal))
            {
                string key = values[index][2..];
                if (index + 1 < values.Length && !values[index + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    result[key] = values[++index];
                }
                else
                {
                    result[key] = "true";
                }
            }
        }

        return result;
    }

    private static string ResolveLanguage(Dictionary<string, string> arguments)
    {
        string requested = arguments.TryGetValue("language", out string? language)
            ? language
            : CultureInfo.CurrentUICulture.Name;
        return string.Equals(requested, "zh-CN", StringComparison.OrdinalIgnoreCase) ? "zh-CN" : "en-US";
    }

    private static ApplicationTheme ResolveTheme(string requested)
    {
        if (string.Equals(requested, "Light", StringComparison.OrdinalIgnoreCase))
        {
            return ApplicationTheme.Light;
        }

        if (string.Equals(requested, "Dark", StringComparison.OrdinalIgnoreCase))
        {
            return ApplicationTheme.Dark;
        }

        return ApplicationThemeManager.GetSystemTheme() == SystemTheme.Light
            ? ApplicationTheme.Light
            : ApplicationTheme.Dark;
    }

    private static string ResolveThemeMode(Dictionary<string, string> arguments, string persistedThemeMode)
    {
        string requested = arguments.TryGetValue("theme", out string? theme) ? theme : persistedThemeMode;
        return requested.Equals("Light", StringComparison.OrdinalIgnoreCase) ? "Light" : "Dark";
    }

    private static string LoadPersistedThemeMode()
    {
        try
        {
            string path = Services.AppSettingsService.SettingsFilePath;
            if (!File.Exists(path))
            {
                return "Dark";
            }

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty("ThemeMode", out JsonElement value)
                ? value.GetString() ?? "Dark"
                : "Dark";
        }
        catch (Exception exception)
        {
            Program.DiagnosticLog?.Warning($"Failed to read startup theme from settings. {exception.Message}");
            return "Dark";
        }
    }

    internal void ApplyTheme(string mode)
    {
        CurrentThemeMode = string.Equals(mode, "Light", StringComparison.OrdinalIgnoreCase) ? "Light" : "Dark";
        ApplicationTheme theme = CurrentThemeMode switch
        {
            "Light" => ApplicationTheme.Light,
            _ => ApplicationTheme.Dark,
        };
        ApplicationThemeManager.Apply(theme, WindowBackdropType.Mica, true);
    }

    private void OnApplicationThemeChanged(ApplicationTheme currentApplicationTheme, System.Windows.Media.Color availableAccent) =>
        ApplyDuComColorTokens(currentApplicationTheme);

    private void ApplyDuComColorTokens(ApplicationTheme applicationTheme)
    {
        string tokenSuffix = applicationTheme == ApplicationTheme.Light ? "Light" : "Dark";
        System.Collections.ObjectModel.Collection<ResourceDictionary> dictionaries = Resources.MergedDictionaries;
        for (int index = 0; index < dictionaries.Count; index++)
        {
            string? original = dictionaries[index].Source?.OriginalString;
            if (original is not null && original.Contains("DesignTokens.Colors", StringComparison.OrdinalIgnoreCase))
            {
                if (!original.Contains($".{tokenSuffix}.", StringComparison.Ordinal))
                {
                    dictionaries[index] = new ResourceDictionary
                    {
                        Source = new Uri($"Resources/DesignTokens.Colors.{tokenSuffix}.xaml", UriKind.Relative),
                    };
                }

                return;
            }
        }

        DiagnosticLog?.Warning($"DuCom color token dictionary was not found when applying the {tokenSuffix} palette.");
    }

    private void RunShellSmokeTest(Window window)
    {
        try
        {
            if (window.ResizeMode != ResizeMode.CanResize || window.MinWidth != 960 || window.MinHeight != 640)
            {
                throw new InvalidOperationException("Window resize contract is not configured correctly.");
            }

            if (window is not MainWindow shellWindow)
            {
                throw new InvalidOperationException("Shell smoke test requires the DuCom main window.");
            }

            string layoutResults = shellWindow.ValidateShellLayouts();
            DiagnosticLog?.Information(
                $"Shell smoke test passed. ResizeMode={window.ResizeMode}; Layouts={layoutResults}.");
            Shutdown(0);
        }
        catch (Exception exception)
        {
            DiagnosticLog?.Error("Shell smoke test failed.", exception);
            Shutdown(-2);
        }
    }

    private async void RunAboutSmokeTest(Window owner)
    {
        try
        {
            using AboutWindow about = new() { Owner = owner };
            about.Show();
            await Task.Delay(1_200);
            if (about.DataContext is not ViewModels.AboutViewModel viewModel || string.IsNullOrWhiteSpace(viewModel.CurrentTime))
            {
                throw new InvalidOperationException("About window real-time clock did not initialize.");
            }

            DiagnosticLog?.Information($"About smoke test passed. CurrentTime={viewModel.CurrentTime}.");
            about.Close();
            Shutdown(0);
        }
        catch (Exception exception)
        {
            DiagnosticLog?.Error("About smoke test failed.", exception);
            Shutdown(-3);
        }
    }

    private void RunRulesSmokeTest(Window owner)
    {
        string cleanProfileDirectory = Path.Combine(Path.GetTempPath(), $"DuComRulesSmoke-{Guid.NewGuid():N}");
        try
        {
            if (owner.DataContext is not MainViewModel viewModel)
            {
                throw new InvalidOperationException("Rules smoke requires MainViewModel.");
            }

            HighlightFilterRulesViewModel.RuleProjectEditor? defaultProject = viewModel.HighlightFilterSettings.Projects.FirstOrDefault(project =>
                string.Equals(project.Name, "default", StringComparison.OrdinalIgnoreCase));
            if (defaultProject is null || defaultProject.Rules.Count == 0)
            {
                throw new InvalidOperationException("Default highlight-rule project was not initialized in the rules editor.");
            }

            HighlightFilterRuleProject? runtimeProject = viewModel.HighlightRuleProjects.FirstOrDefault(project =>
                string.Equals(project.Name, "default", StringComparison.OrdinalIgnoreCase));
            if (runtimeProject is null || runtimeProject.Rules.Count == 0)
            {
                throw new InvalidOperationException("Default highlight-rule project was not loaded into the runtime rule collection.");
            }

            string cleanRulesPath = Path.Combine(cleanProfileDirectory, "DuCom", "highlight-filter-rules.json");
            HighlightFilterRuleService cleanService = new(cleanRulesPath);
            HighlightFilterRulesViewModel cleanViewModel = new(cleanService);
            HighlightFilterRulesViewModel.RuleProjectEditor? cleanDefaultProject = cleanViewModel.Projects.FirstOrDefault(project =>
                string.Equals(project.Name, "default", StringComparison.OrdinalIgnoreCase));
            if (!File.Exists(cleanRulesPath) || cleanDefaultProject is null || cleanDefaultProject.Rules.Count == 0)
            {
                throw new InvalidOperationException("A clean profile did not create and expose the default highlight-rule project.");
            }

            DiagnosticLog?.Information(
                $"Rules smoke test passed. EditorRules={defaultProject.Rules.Count}; RuntimeRules={runtimeProject.Rules.Count}; CleanProfileRules={cleanDefaultProject.Rules.Count}.");
            Shutdown(0);
        }
        catch (Exception exception)
        {
            DiagnosticLog?.Error("Rules smoke test failed.", exception);
            Shutdown(-8);
        }
        finally
        {
            try
            {
                if (Directory.Exists(cleanProfileDirectory))
                {
                    Directory.Delete(cleanProfileDirectory, recursive: true);
                }
            }
            catch (Exception exception)
            {
                DiagnosticLog?.Warning($"Rules smoke cleanup failed. {exception.Message}");
            }
        }
    }

    private async void RunToolsSmokeTest(Window owner)
    {
        MainViewModel? mainViewModel = owner.DataContext as MainViewModel;
        ShortcutManager? shortcutManager = mainViewModel?.ShortcutManager;
        try
        {
            // Per-page identity verification: the page key must select the tab whose header
            // matches that page's header resource — a stale index mapping fails here.
            foreach (string page in ToolCenterPages.All)
            {
                ToolCenterWindow window = new(page, shortcutManager, mainViewModel?.CommandRunner, mainViewModel, mainViewModel?.Telnet) { Owner = owner };
                window.Show();
                await Task.Delay(100);
                int expectedIndex = ToolCenterPages.IndexOf(page);
                System.Windows.Controls.TabControl tabs = window.RootTabs;
                if (tabs.SelectedIndex != expectedIndex)
                {
                    throw new InvalidOperationException($"Tools page '{page}' selected tab {tabs.SelectedIndex}, expected {expectedIndex}.");
                }

                if (tabs.SelectedItem is not System.Windows.Controls.TabItem selected ||
                    selected.Header is not string header ||
                    string.IsNullOrWhiteSpace(header))
                {
                    throw new InvalidOperationException($"Tools page '{page}' has no readable tab header.");
                }

                string expectedHeader = owner.TryFindResource(ToolCenterPages.HeaderResourceKey(page)) as string ?? string.Empty;
                if (!string.Equals(header, expectedHeader, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"Tools page '{page}' header '{header}' does not match the expected page identity '{expectedHeader}'.");
                }

                if (page == ToolCenterPages.Telnet)
                {
                    await window.SmokeTelnetAsync();
                }

                window.Close();
                await Task.Delay(50);
            }

            // Ownership guard (review five): tool windows must never dispose the shared runner.
            // A start attempt with no open session must return false gracefully — an accidental
            // disposal by any of the windows above would surface as ObjectDisposedException.
            if (mainViewModel is null)
            {
                throw new InvalidOperationException("Tools smoke requires MainViewModel.");
            }

            await mainViewModel.CommandRunner.StopAsync();
            if (mainViewModel.CommandRunner.Start(DuCom.Core.Sending.CommandGroup.Create("tools-smoke-probe")))
            {
                throw new InvalidOperationException("Runner accepted a group although no session is open.");
            }

            DiagnosticLog?.Information("Tools smoke runner ownership verified.");

            ApplicationThemeManager.Apply(ApplicationTheme.Light, WindowBackdropType.Mica, true);
            ApplicationThemeManager.Apply(ApplicationTheme.Dark, WindowBackdropType.Mica, true);
            DiagnosticLog?.Information("Tools smoke test passed.");
            Shutdown(0);
        }
        catch (Exception exception)
        {
            DiagnosticLog?.Error("Tools smoke test failed.", exception);
            Shutdown(-4);
        }
    }

    private async void RunSplitSmokeTest(Window window)
    {
        try
        {
            if (window.DataContext is not ViewModels.MainViewModel viewModel)
            {
                throw new InvalidOperationException("Split smoke requires MainViewModel.");
            }

            SessionViewModel[] openSessions = [.. viewModel.Sessions.Where(session => session.IsOpen).Take(3)];
            if (openSessions.Length < 2)
            {
                DiagnosticLog?.Information("Split smoke skipped because two open sessions are not available; no hardware port is opened by smoke tests.");
                Shutdown(0);
                return;
            }

            viewModel.SelectedSession = openSessions[0];
            string portName = openSessions[1].PortName;
            await viewModel.AssignRightPaneAsync(portName);
            if (!viewModel.IsSplitView || viewModel.SelectedRightSession?.PortName != portName)
            {
                throw new InvalidOperationException("Right split pane did not bind the dropped port session.");
            }

            SessionViewModel splitSession = viewModel.SelectedRightSession;
            if (!splitSession.IsInRightPane || !splitSession.IsOpen)
            {
                throw new InvalidOperationException("Right split pane did not preserve the open session state.");
            }

            viewModel.MoveRightSessionToMainCommand.Execute(splitSession);
            if (splitSession.IsInRightPane || !splitSession.IsOpen || !viewModel.Sessions.Contains(splitSession))
            {
                throw new InvalidOperationException("Move-to-main did not preserve the right session.");
            }
            await viewModel.AssignRightPaneAsync(portName);
            splitSession = viewModel.SelectedRightSession!;

            if (openSessions.Length >= 3)
            {
                await viewModel.AssignRightPaneAsync(openSessions[2].PortName);
                if (viewModel.RightSessions.Count != 2 || !viewModel.RightSessions.Contains(splitSession))
                {
                    throw new InvalidOperationException("Right split pane did not retain multiple session tabs.");
                }

                await viewModel.CloseRightPaneCommand.ExecuteAsync(null);
                if (!viewModel.IsSplitView || viewModel.RightSessions.Count != 1 || viewModel.SelectedRightSession != splitSession || viewModel.Sessions.Contains(openSessions[2]))
                {
                    throw new InvalidOperationException("Closing one right tab did not fully remove only the selected right session.");
                }
            }

            await viewModel.CloseRightPaneCommand.ExecuteAsync(null);
            if (viewModel.IsSplitView)
            {
                throw new InvalidOperationException("Right split pane did not close.");
            }

            if (splitSession.IsInRightPane || splitSession.IsOpen || viewModel.Sessions.Contains(splitSession))
            {
                throw new InvalidOperationException("Closing the right pane did not close, dispose, and remove its serial session.");
            }

            DiagnosticLog?.Information("Split smoke test passed.");
            Shutdown(0);
        }
        catch (Exception exception)
        {
            DiagnosticLog?.Error("Split smoke test failed.", exception);
            Shutdown(-5);
        }
    }

    private async void RunSettingsSmokeTest(Window owner)
    {
        try
        {
            SettingsWindow settings = new(selectedCategory: 1, transportOnly: true)
            {
                Owner = owner,
                DataContext = owner.DataContext,
            };
            settings.Show();
            await Task.Delay(300);
            if (!settings.IsVisible || settings.ActualWidth < settings.MinWidth || settings.ActualHeight < settings.MinHeight)
            {
                throw new InvalidOperationException("Settings window did not initialize at its minimum usable size.");
            }

            if (settings.DtrEnableCheckBox.Visibility != Visibility.Visible ||
                settings.RtsEnableCheckBox.Visibility != Visibility.Visible ||
                settings.DiscardNullCheckBox.Visibility != Visibility.Visible ||
                settings.DtrEnableCheckBox.Content is not string dtr || string.IsNullOrWhiteSpace(dtr) ||
                settings.RtsEnableCheckBox.Content is not string rts || string.IsNullOrWhiteSpace(rts) ||
                settings.DiscardNullCheckBox.Content is not string discardNull || string.IsNullOrWhiteSpace(discardNull))
            {
                throw new InvalidOperationException("Serial line-control settings are not visible or localized.");
            }

            settings.Close();

            SettingsWindow generalSettings = new(selectedCategory: 0)
            {
                Owner = owner,
                DataContext = owner.DataContext,
            };
            generalSettings.Show();
            await Task.Delay(200);
            if (generalSettings.PrivateMemoryMonitorCheckBox.Content is not string monitorLabel ||
                string.IsNullOrWhiteSpace(monitorLabel) ||
                generalSettings.PrivateMemoryThresholdTextBox.Text.Length == 0)
            {
                throw new InvalidOperationException("Private-memory monitor settings are not visible or localized.");
            }

            if (owner.DataContext is not MainViewModel mainViewModel || !mainViewModel.HasPrivateMemoryMonitor)
            {
                throw new InvalidOperationException("Application-owned private-memory monitor is not attached.");
            }

            generalSettings.Close();
            DiagnosticLog?.Information("Settings smoke test passed.");
            Shutdown(0);
        }
        catch (Exception exception)
        {
            DiagnosticLog?.Error("Settings smoke test failed.", exception);
            Shutdown(-6);
        }
    }

    private async void RunEditorSmokeTest(Window owner)
    {
        try
        {
            System.Collections.ObjectModel.ObservableCollection<LogLineViewModel> lines = [];
            for (int index = 0; index < 200; index++)
            {
                string text = $"existing-{index:D4}";
                lines.Add(new LogLineViewModel(
                    index + 1,
                    0,
                    DateTimeOffset.UtcNow,
                    DuCom.Core.Storage.LineDirection.Rx,
                    text,
                    [new DuCom.Core.Parsing.StyleRun(text, null, null, null, null, null, null, false, false, false)]));
            }

            Controls.BoundedLogEditor editor = new()
            {
                Lines = lines,
                FollowEnd = true,
                Foreground = System.Windows.Media.Brushes.White,
                Background = System.Windows.Media.Brushes.Black,
            };
            Window probe = new()
            {
                Owner = owner,
                Width = 640,
                Height = 420,
                Content = editor,
                ShowInTaskbar = false,
            };
            probe.Show();
            await Task.Delay(250);
            if (!editor.IsReadOnly || !editor.Document.Text.Contains("existing-0199", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("AvalonEdit did not project the existing log snapshot.");
            }

            System.Collections.ObjectModel.ObservableCollection<LogLineViewModel> initiallyPausedLines = [];
            string initiallyPausedText = "initially-paused";
            initiallyPausedLines.Add(new LogLineViewModel(
                1,
                0,
                DateTimeOffset.UtcNow,
                DuCom.Core.Storage.LineDirection.Rx,
                initiallyPausedText,
                [new DuCom.Core.Parsing.StyleRun(initiallyPausedText, null, null, null, null, null, null, false, false, false)]));
            Controls.BoundedLogEditor initiallyPausedEditor = new()
            {
                Lines = initiallyPausedLines,
                FollowEnd = false,
                Foreground = System.Windows.Media.Brushes.White,
                Background = System.Windows.Media.Brushes.Black,
            };
            Window initiallyPausedProbe = new()
            {
                Owner = owner,
                Width = 480,
                Height = 240,
                Content = initiallyPausedEditor,
                ShowInTaskbar = false,
            };
            initiallyPausedProbe.Show();
            await Task.Delay(200);
            if (!initiallyPausedEditor.Document.Text.Contains(initiallyPausedText, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("AvalonEdit did not project initial content while follow mode was disabled.");
            }
            initiallyPausedProbe.Close();

            for (int index = 0; index < 300; index++)
            {
                string text = $"live-{index:D4}";
                lines.Add(new LogLineViewModel(
                    201 + index,
                    0,
                    DateTimeOffset.UtcNow,
                    DuCom.Core.Storage.LineDirection.Rx,
                    text,
                    [new DuCom.Core.Parsing.StyleRun(text, null, null, null, null, null, null, false, false, false)]));
            }
            for (int index = 0; index < 120; index++)
            {
                lines.RemoveAt(0);
            }

            await Task.Delay(250);
            if (!editor.Document.Text.Contains("live-0299", StringComparison.Ordinal) ||
                editor.Document.Text.Contains("existing-0000", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("AvalonEdit did not apply live append and prefix eviction correctly.");
            }

            int selectionStart = editor.Document.Text.IndexOf("live-0299", StringComparison.Ordinal);
            editor.Select(selectionStart, "live-0299".Length);
            if (!string.Equals(editor.SelectedText, "live-0299", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("AvalonEdit native text selection is not operational.");
            }

            editor.PauseFollow();
            editor.FollowEnd = false;
            double pausedOffset = editor.VerticalOffset;
            double pausedExtent = editor.ExtentHeight;
            for (int index = 0; index < 100; index++)
            {
                string text = $"paused-{index:D4}";
                lines.Add(new LogLineViewModel(
                    501 + index,
                    0,
                    DateTimeOffset.UtcNow,
                    DuCom.Core.Storage.LineDirection.Rx,
                    text,
                    [new DuCom.Core.Parsing.StyleRun(text, null, null, null, null, null, null, false, false, false)]));
            }
            await Task.Delay(250);
            if (Math.Abs(editor.VerticalOffset - pausedOffset) > 0.1d)
            {
                throw new InvalidOperationException("AvalonEdit continued following the end after follow mode was disabled.");
            }

            if (!editor.Document.Text.Contains("paused-0099", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("AvalonEdit stopped updating the document while follow mode was disabled.");
            }
            if (editor.ExtentHeight <= pausedExtent)
            {
                throw new InvalidOperationException("AvalonEdit did not update the scrollbar extent while follow mode was disabled.");
            }

            editor.FollowEnd = true;
            editor.ResumeFollow();
            await Task.Delay(250);

            probe.Close();
            DiagnosticLog?.Information("AvalonEdit log smoke test passed.");
            Shutdown(0);
        }
        catch (Exception exception)
        {
            DiagnosticLog?.Error("AvalonEdit log smoke test failed.", exception);
            Shutdown(-7);
        }
    }
}
