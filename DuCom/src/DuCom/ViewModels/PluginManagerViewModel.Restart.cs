using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using DuCom.PluginHost;
using DuCom.PluginHost.Core;
using DuCom.Services.Plugins;

namespace DuCom.ViewModels;

public partial class PluginManagerViewModel
{
    [RelayCommand]
    private async Task RestartPluginAsync(PluginManagerRow? row)
    {
        if (row is null) return;
        SelectedPlugin = row;
        if (IsChangingState || _mainViewModel.PluginSystem is not { } system)
        {
            return;
        }

        PluginManagerRow? currentRow = system.Service.BuildManagerRows().FirstOrDefault(item => item.Id == row.Id);
        if (currentRow is null
            || currentRow.State is PluginRuntimeState.Starting or PluginRuntimeState.Activating or PluginRuntimeState.Stopping)
        {
            return;
        }

        if (system.Service.SafeStartAllPlugins)
        {
            OperationMessage = Resource("Plugins.SafeStart.Blocked");
            return;
        }

        IsChangingState = true;
        OperationMessage = Resource("Plugins.Working");
        try
        {
            // Works from ON, OFF, and fault-disabled states alike: stop a live worker first,
            // clear a persisted fault after the same confirmation the ON switch uses, then start.
            if (currentRow.State == PluginRuntimeState.FaultDisabled)
            {
                if (!ThemedMessageDialog.Confirm(Application.Current?.MainWindow,
                        string.Format(Resource("Plugins.Retry.Warn"), row.Id, row.FaultReason),
                        Resource("Plugins.Retry.Title"), "Dialog.Retry", "Dialog.Cancel"))
                {
                    OperationMessage = string.Empty;
                    return;
                }

                system.Service.ClearFaultDisable(row.Id);
            }

            if (currentRow.IsActive)
            {
                await system.Service.StopAsync(row.Id);
            }

            system.Service.SetEnabled(row.Id, true);
            bool started = await system.Service.StartRegisteredAsync(row.Id);
            if (!started)
            {
                PluginManagerRow? current = system.Service.BuildManagerRows().FirstOrDefault(item => item.Id == row.Id);
                OperationMessage = current?.FaultReason ?? Resource("Plugins.StartFailed");
                return;
            }

            OperationMessage = Resource("Plugins.Restarted");
        }
        catch (Exception exception)
        {
            Program.DiagnosticLog?.Error("Plugin restart failed.", exception);
            OperationMessage = exception.Message;
        }
        finally
        {
            IsChangingState = false;
            RefreshPlugins();
        }
    }

    [RelayCommand]
    private async Task ApplyAndRestartAsync()
    {
        PluginSystemHost? system = _mainViewModel.PluginSystem;
        if (system is null || !Plugins.Any(row => row.PendingUpdate))
        {
            OperationMessage = Resource("Plugins.ApplyRestart.None");
            return;
        }

        if (!ThemedMessageDialog.Confirm(
                Application.Current?.MainWindow,
                Resource("Plugins.ApplyRestart.Confirm"),
                Resource("Plugins.ApplyRestart.Title"),
                "Dialog.Restart",
                "Dialog.Cancel"))
        {
            return;
        }

        IsChangingState = true;
        OperationMessage = Resource("Plugins.Working");
        (bool succeeded, string message) = await system.ApplyAndRestartAsync(
            CloseAllSessionsForRestartAsync,
            SaveSettingsForRestartAsync,
            RestartApplication);
        if (!succeeded)
        {
            OperationMessage = message;
            IsChangingState = false;
        }
    }

    private async Task<bool> CloseAllSessionsForRestartAsync()
    {
        try
        {
            await _mainViewModel.CloseAllSessionsForRestartAsync();
            return true;
        }
        catch (Exception exception)
        {
            Program.DiagnosticLog?.Error("Core cleanup failed before plugin apply-and-restart; restart aborted.", exception);
            ThemedMessageDialog.Show(
                Application.Current?.MainWindow,
                Resource("Plugins.ApplyRestart.CoreCleanupFailed"),
                Resource("Plugins.ApplyRestart.Title"),
                ThemedMessageDialogKind.Error);
            return false;
        }
    }

    private Task SaveSettingsForRestartAsync()
    {
        _mainViewModel.SaveSettingsNow();
        return Task.CompletedTask;
    }

    private Task RestartApplication()
    {
        string? executable = Environment.ProcessPath;
        if (executable is null)
        {
            throw new InvalidOperationException("Cannot determine the current executable path.");
        }

        Process.Start(new ProcessStartInfo(executable, $"--wait-parent {Environment.ProcessId}") { UseShellExecute = true });
        Application.Current.Shutdown(0);
        return Task.CompletedTask;
    }
}
