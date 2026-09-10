using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DuCom.Core.Parsing;
using DuCom.Services.Shortcuts;
using DuCom.ViewModels;
using Wpf.Ui.Appearance;
using WindowBackdropType = Wpf.Ui.Controls.WindowBackdropType;

namespace DuCom;

public partial class App
{
    private async void RunPluginsSmokeTest(Window owner)
    {
        try
        {
            for (int attempt = 0; attempt < 40; attempt++)
            {
                await Task.Delay(250);
                if (_pluginSystemHost is null)
                {
                    break;
                }

                if (_pluginSystemHost.Service.BuildManagerRows().Count(row => row.State == PluginHost.Core.PluginRuntimeState.Active) == PluginHost.PluginSystemService.FactoryIds.Count)
                {
                    break;
                }
            }

            if (_pluginSystemHost is null)
            {
                throw new InvalidOperationException("The plugin system did not initialize.");
            }

            IReadOnlyList<PluginHost.PluginManagerRow> rows = _pluginSystemHost.Service.BuildManagerRows();
            int active = rows.Count(row => row.State == PluginHost.Core.PluginRuntimeState.Active);
            int faulted = _pluginSystemHost.Service.BuildManagerRows().Count(row => row.State == PluginHost.Core.PluginRuntimeState.FaultDisabled);
            if (active != PluginHost.PluginSystemService.FactoryIds.Count || faulted != 0)
            {
                throw new InvalidOperationException($"Built-in plugin activation is incomplete. Active={active}; Faulted={faulted}; Required={PluginHost.PluginSystemService.FactoryIds.Count}.");
            }

            DiagnosticLog?.Information($"Plugins smoke test passed. Active={active}; Faulted={faulted}; Rows={string.Join(", ", rows.Select(row => $"{row.Id}={row.State}"))}.");
            Shutdown(0);
        }
        catch (Exception exception)
        {
            DiagnosticLog?.Error("Plugins smoke test failed.", exception);
            Shutdown(-11);
        }
    }

    private async void RunPluginUiSmokeTest(Window owner)
    {
        try
        {
            for (int attempt = 0; attempt < 60 && (_pluginSystemHost?.Ui.Activations.Count ?? 0) < PluginHost.PluginSystemService.FactoryIds.Count; attempt++)
            {
                await Task.Delay(200);
            }
            if (_pluginSystemHost is null) throw new InvalidOperationException("Plugin system is unavailable.");

            foreach (PluginHost.PluginManagerRow row in _pluginSystemHost.Service.BuildManagerRows().Where(row => row.IsBuiltIn))
            {
                PluginHost.PluginPublishedActivation activation = _pluginSystemHost.Ui.Activations.Single(item => item.PluginId == row.Id);
                string page = activation.ToolPages.Single().ContributionId;
                _pluginSystemHost.Ui.OpenToolPage(row.Id, page, async (command, values) =>
                    await (_pluginSystemHost.Service.GetOrCreateController(row.Id)?.InvokeCommandAsync(command, values) ?? Task.FromResult(false)));
                await Task.Delay(200);
                Services.Plugins.PluginToolWindow window = Current.Windows.OfType<Services.Plugins.PluginToolWindow>().Single(item => item.PluginId == row.Id);
                if (window.Content is not Grid shell || shell.Children.OfType<Wpf.Ui.Controls.TitleBar>().Count() != 1
                    || !shell.Children.OfType<Border>().Any(footer => footer.Child is System.Windows.Controls.Button button && Equals(button.Content, FindResource("LogPackage.Cancel"))))
                {
                    throw new InvalidOperationException($"Plugin page shell is incomplete for {row.Id}.");
                }
                if (row.Id == "com.ducom.log-package" && !Descendants<Border>(window).Any(border => ReferenceEquals(border.Style, FindResource("Style.SettingsCard"))))
                {
                    throw new InvalidOperationException("Log package page did not use settings-card layout.");
                }
                window.Close();
            }

            System.Windows.Controls.Button logEntry = Descendants<System.Windows.Controls.Button>(owner)
                .Single(button => Equals(button.Command, _compositionRoot!.MainViewModel.ShowLogPackageCommand));
            if (logEntry.Visibility != Visibility.Visible) throw new InvalidOperationException("Active log-package plugin did not expose the title-bar entry.");
            _pluginSystemHost.Service.SetEnabled("com.ducom.log-package", false, stopImmediately: false);
            await _pluginSystemHost.Service.StopAsync("com.ducom.log-package");
            DoEvents();
            if (logEntry.Visibility == Visibility.Visible) throw new InvalidOperationException("Disabled log-package plugin left the title-bar entry visible.");

            DiagnosticLog?.Information("Plugin UI smoke test passed. Built-in pages opened with themed shell and close actions; log entry followed Active state.");
            Shutdown(0);
        }
        catch (Exception exception)
        {
            DiagnosticLog?.Error("Plugin UI smoke test failed.", exception);
            Shutdown(-12);
        }
    }

}
