using System.IO;
using System.Windows;
using DuCom.Core.Parsing;
using DuCom.Services.Shortcuts;
using DuCom.ViewModels;
using Wpf.Ui.Appearance;
using WindowBackdropType = Wpf.Ui.Controls.WindowBackdropType;

namespace DuCom;

public partial class App
{
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
                ToolCenterWindow window = new(page, shortcutManager, mainViewModel, mainViewModel?.Telnet) { Owner = owner };
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
    {        try
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
}
