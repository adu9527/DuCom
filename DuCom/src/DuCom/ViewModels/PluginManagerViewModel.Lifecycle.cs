using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using DuCom.PluginHost;
using DuCom.PluginHost.Core;
using DuCom.Services.Plugins;

namespace DuCom.ViewModels;

public partial class PluginManagerViewModel
{
    private PluginSystemHost? _attachedSystem;
    private DispatcherOperation? _pendingRefresh;

    internal void AttachPluginSystem(PluginSystemHost system)
    {
        if (ReferenceEquals(_attachedSystem, system))
        {
            return;
        }

        if (_attachedSystem is not null)
        {
            _attachedSystem.Service.Changed -= ScheduleRefresh;
        }

        _attachedSystem = system;
        system.Service.Changed += ScheduleRefresh;
        RefreshPlugins();
    }

    [RelayCommand]
    private void RefreshPlugins()
    {
        PluginSystemHost? system = _mainViewModel.PluginSystem;
        string? selectedId = SelectedPlugin?.Id;
        Plugins.Clear();
        if (system is not null)
        {
            foreach (PluginManagerRow row in system.Service.BuildManagerRows().OrderBy(row => row.Id, StringComparer.Ordinal))
            {
                Plugins.Add(row);
            }
        }

        SelectedPlugin = Plugins.FirstOrDefault(row => string.Equals(row.Id, selectedId, StringComparison.Ordinal))
            ?? Plugins.FirstOrDefault();
        OnPropertyChanged(nameof(BudgetSummary));
        OnPropertyChanged(nameof(HasPendingUpdates));
        OnPropertyChanged(nameof(InstalledDirectory));
        OnPropertyChanged(nameof(SafeStartAllPlugins));
    }

    [RelayCommand]
    private void OpenPluginFolder()
    {
        if (Directory.Exists(InstalledDirectory))
        {
            Process.Start(new ProcessStartInfo(InstalledDirectory) { UseShellExecute = true });
        }
    }

    [RelayCommand]
    private async Task ToggleSelectedAsync()
    {
        if (SelectedPlugin is { } row) await ChangeStateAsync(row, !row.IsActive);
    }

    private async Task ChangeStateAsync(PluginManagerRow row, bool enable)
    {
        if (IsChangingState || _mainViewModel.PluginSystem is not { } system)
        {
            return;
        }

        PluginManagerRow? currentRow = system.Service.BuildManagerRows().FirstOrDefault(item => item.Id == row.Id);
        if (currentRow is null || currentRow.State is PluginRuntimeState.Starting or PluginRuntimeState.Activating or PluginRuntimeState.Stopping
            || currentRow.IsActive == enable)
        {
            return;
        }

        IsChangingState = true;
        OperationMessage = Resource("Plugins.Working");
        try
        {
            if (!enable)
            {
                system.Service.SetEnabled(row.Id, false, stopImmediately: false);
                await system.Service.StopAsync(row.Id);
            }
            else
            {
                if (system.Service.SafeStartAllPlugins)
                {
                    OperationMessage = Resource("Plugins.SafeStart.Blocked");
                    return;
                }

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

                system.Service.SetEnabled(row.Id, true);
                bool started = await system.Service.StartRegisteredAsync(row.Id);
                if (!started)
                {
                    PluginManagerRow? current = system.Service.BuildManagerRows().FirstOrDefault(item => item.Id == row.Id);
                    OperationMessage = current?.FaultReason ?? Resource("Plugins.StartFailed");
                    return;
                }
            }

            OperationMessage = Resource(enable ? "Plugins.Started" : "Plugins.Stopped");
        }
        catch (Exception exception)
        {
            Program.DiagnosticLog?.Error("Plugin state change failed.", exception);
            OperationMessage = exception.Message;
        }
        finally
        {
            IsChangingState = false;
            RefreshPlugins();
        }
    }

    [RelayCommand]
    private async Task TogglePluginAsync(PluginManagerRow? row)
    {
        if (row is null) return;
        SelectedPlugin = row;
        await ToggleSelectedAsync();
    }

    [RelayCommand]
    private async Task EnablePluginAsync(PluginManagerRow? row)
    {
        if (row is null) return;
        await ChangeStateAsync(row, true);
    }

    [RelayCommand]
    private async Task DisablePluginAsync(PluginManagerRow? row)
    {
        if (row is null) return;
        await ChangeStateAsync(row, false);
    }

    [RelayCommand]
    private async Task RetryPluginAsync(PluginManagerRow? row)
    {
        if (row is null) return;
        SelectedPlugin = row;
        if (row.State is PluginRuntimeState.FaultDisabled or PluginRuntimeState.Rejected)
        {
            await ToggleSelectedAsync();
        }
    }

    [RelayCommand]
    private async Task OpenPluginAsync(PluginManagerRow? row)
    {
        if (row is null || _mainViewModel.PluginSystem is not { } system) return;
        PluginPublishedActivation? activation = system.Ui.Activations.FirstOrDefault(item => item.PluginId == row.Id);
        if (activation is null)
        {
            OperationMessage = Resource("Plugins.NotActive");
            return;
        }

        string? page = activation.ToolPages.Count > 0 ? activation.ToolPages[0].ContributionId : null;
        if (!string.IsNullOrEmpty(page))
        {
            // Refresh the plugin's cached page first (same as the menu entry) so the window
            // opens with live state instead of the last pushed snapshot.
            try
            {
                await _mainViewModel.InvokePluginCommandForManagerAsync(row.Id, "open", new Dictionary<string, string>());
            }
            catch (Exception exception)
            {
                Program.DiagnosticLog?.Warning($"Plugin open command failed: {exception.Message}");
            }

            system.Ui.OpenToolPage(row.Id, page, (commandId, values) => _mainViewModel.InvokePluginCommandForManagerAsync(row.Id, commandId, values));
            return;
        }

        OperationMessage = Resource("Plugins.NotActive");
    }

    private void ScheduleRefresh()
    {
        Dispatcher? dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || _pendingRefresh is { Status: DispatcherOperationStatus.Pending })
        {
            return;
        }

        _pendingRefresh = dispatcher.BeginInvoke(() =>
        {
            _pendingRefresh = null;
            RefreshPlugins();
        }, DispatcherPriority.Background);
    }
}
