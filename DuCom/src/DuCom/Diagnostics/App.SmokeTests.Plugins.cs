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

            foreach (PluginHost.PluginManagerRow builtIn in _pluginSystemHost.Service.BuildManagerRows().Where(row => row.IsBuiltIn))
            {
                if (!_pluginSystemHost.Ui.Activations.Any(item => item.PluginId == builtIn.Id))
                {
                    _pluginSystemHost.Service.SetEnabled(builtIn.Id, true, stopImmediately: false);
                    await _pluginSystemHost.Service.StartRegisteredAsync(builtIn.Id);
                }
            }
            await Task.Delay(200);

            foreach (PluginHost.PluginManagerRow row in _pluginSystemHost.Service.BuildManagerRows().Where(row => row.IsBuiltIn))
            {
                PluginHost.PluginPublishedActivation activation = _pluginSystemHost.Ui.Activations.Single(item => item.PluginId == row.Id);
                string page = activation.ToolPages.Single().ContributionId;
                _pluginSystemHost.Ui.OpenToolPage(row.Id, page, async (command, values) =>
                    await (_pluginSystemHost.Service.GetOrCreateController(row.Id)?.InvokeCommandAsync(command, values) ?? Task.FromResult(false)));
                await Task.Delay(200);
                if (row.Id == "com.ducom.production")
                {
                    Services.Plugins.ProductionWindow production = Current.Windows.OfType<Services.Plugins.ProductionWindow>().Single();
                    await VerifyIndependentPluginWindowAsync(owner, production);
                    production.Close();
                    await Task.Delay(200);
                    VerifyMainWindowUnchanged(owner);
                    continue;
                }
                if (row.Id == "com.ducom.checksum")
                {
                    Services.Plugins.ChecksumWindow checksum = Current.Windows.OfType<Services.Plugins.ChecksumWindow>().Single();
                    if (checksum.Width != 700 || checksum.Height != 480 || !checksum.AllowDrop)
                        throw new InvalidOperationException("Checksum window did not preserve original size/drop behavior.");
                    await VerifyIndependentPluginWindowAsync(owner, checksum);
                    checksum.Close();
                    VerifyMainWindowUnchanged(owner);
                    continue;
                }
                if (row.Id == "com.ducom.crc")
                {
                    Services.Plugins.CrcWindow crc = Current.Windows.OfType<Services.Plugins.CrcWindow>().Single();
                    await VerifyIndependentPluginWindowAsync(owner, crc);
                    crc.Close();
                    VerifyMainWindowUnchanged(owner);
                    continue;
                }
                Services.Plugins.PluginToolWindow window = Current.Windows.OfType<Services.Plugins.PluginToolWindow>().Single(item => item.PluginId == row.Id);
                FeedbackWindow feedback = FeedbackWindow.ShowOrActivate();
                if (!ReferenceEquals(feedback, FeedbackWindow.ShowOrActivate()))
                    throw new InvalidOperationException("Repeated feedback action created duplicate windows.");
                if (feedback.Owner is not null || !IsWindowEnabled(new System.Windows.Interop.WindowInteropHelper(window).Handle)
                    || !IsWindowEnabled(new System.Windows.Interop.WindowInteropHelper(owner).Handle))
                    throw new InvalidOperationException("Feedback disabled another application window.");
                if (window.Content is not Grid shell || shell.Children.OfType<Wpf.Ui.Controls.TitleBar>().Count() != 1
                    || !Descendants<System.Windows.Controls.Button>(window).Any(button => Equals(button.Content, FindResource("LogPackage.Cancel"))))
                {
                    throw new InvalidOperationException($"Plugin page shell is incomplete for {row.Id}.");
                }
                if (row.Id == "com.ducom.log-package" && !Descendants<Border>(window).Any(border => ReferenceEquals(border.Style, FindResource("Style.SettingsCard"))))
                {
                    throw new InvalidOperationException("Log package page did not use settings-card layout.");
                }
                await VerifyIndependentPluginWindowAsync(owner, window);
                // Menu re-entry must restore an existing minimized tool without
                // bringing the main window out of its independently minimized state.
                owner.WindowState = WindowState.Minimized;
                window.WindowState = WindowState.Minimized;
                _pluginSystemHost.Ui.OpenToolPage(row.Id, page, (_, _) => Task.FromResult(true));
                await Task.Delay(200);
                if (window.WindowState != WindowState.Normal || owner.WindowState != WindowState.Minimized)
                    throw new InvalidOperationException($"Plugin page restore changed the host state for {row.Id}.");
                owner.WindowState = WindowState.Normal;
                window.Close();
                await Task.Delay(200);
                VerifyMainWindowUnchanged(owner);
                if (window.IsVisible) throw new InvalidOperationException("Plugin could not close while feedback was open.");
                feedback.Close();
            }

            System.Windows.Controls.Button logEntry = Descendants<System.Windows.Controls.Button>(owner)
                .Single(button => Equals(button.Command, _compositionRoot!.MainViewModel.ShowLogPackageCommand));
            if (logEntry.Visibility != Visibility.Visible) throw new InvalidOperationException("Active log-package plugin did not expose the title-bar entry.");
            _pluginSystemHost.Service.SetEnabled("com.ducom.log-package", false, stopImmediately: false);
            await _pluginSystemHost.Service.StopAsync("com.ducom.log-package");
            DoEvents();
            if (logEntry.Visibility == Visibility.Visible) throw new InvalidOperationException("Disabled log-package plugin left the title-bar entry visible.");
            _pluginSystemHost.Service.SetEnabled("com.ducom.log-package", true, stopImmediately: false);

            DiagnosticLog?.Information("Plugin UI smoke test passed. Built-in pages opened with themed shell and close actions; log entry followed Active state.");
            // Exercise the same explicit-shutdown path used by plugin updates.
            await PrepareShutdownAsync();
            await PrepareShutdownAsync();
            Shutdown(0);
        }
        catch (Exception exception)
        {
            DiagnosticLog?.Error("Plugin UI smoke test failed.", exception);
            Shutdown(-12);
        }
    }

    private static async Task VerifyIndependentPluginWindowAsync(Window host, Window tool)
    {
        if (tool.Owner is not null || !tool.ShowInTaskbar)
            throw new InvalidOperationException($"Plugin window is not an independent taskbar window: {tool.Title}.");
        bool wasTopmost = tool.Topmost;
        try
        {
            foreach (bool topmost in new[] { false, true })
            {
                tool.Topmost = topmost;
                host.WindowState = WindowState.Minimized;
                await Task.Delay(100);
                if (!tool.IsVisible || tool.WindowState == WindowState.Minimized || tool.Topmost != topmost
                    || IsIconic(new System.Windows.Interop.WindowInteropHelper(tool).Handle))
                    throw new InvalidOperationException($"Minimizing the host also minimized its plugin: {tool.Title}.");
                host.WindowState = WindowState.Normal;
                tool.WindowState = WindowState.Minimized;
                await Task.Delay(100);
                VerifyMainWindowUnchanged(host);
                tool.WindowState = WindowState.Normal;
            }
        }
        finally
        {
            tool.Topmost = wasTopmost;
            host.WindowState = WindowState.Normal;
        }
    }

    private static void VerifyMainWindowUnchanged(Window host)
    {
        if (!host.IsVisible || host.WindowState != WindowState.Normal)
            throw new InvalidOperationException("Plugin window action hid or minimized the main window.");
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool IsIconic(nint window);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(nint window);

}
