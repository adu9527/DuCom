using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DuCom.PluginHost;
using DuCom.Services.Plugins;
using DuCom.PluginHost.Core;
using DuCom.PluginHost.Packages;
using Microsoft.Win32;

namespace DuCom.ViewModels;

public partial class PluginManagerViewModel : ObservableObject
{
    private readonly MainViewModel _mainViewModel;
    private PluginSystemHost? _attachedSystem;

    public PluginManagerViewModel(MainViewModel mainViewModel)
    {
        _mainViewModel = mainViewModel;
        if (mainViewModel.PluginSystem is { } system)
        {
            AttachPluginSystem(system);
        }

        RefreshPlugins();
    }

    internal void AttachPluginSystem(PluginSystemHost system)
    {
        if (ReferenceEquals(_attachedSystem, system))
        {
            return;
        }

        if (_attachedSystem is not null)
        {
            _attachedSystem.Service.Changed -= ScheduleRefresh;
            _attachedSystem.Ui.Changed -= OnUiChanged;
        }

        _attachedSystem = system;
        system.Service.Changed += ScheduleRefresh;
        system.Ui.Changed += OnUiChanged;
        RefreshPlugins();
    }

    private void OnUiChanged(object? sender, EventArgs args) => ScheduleRefresh();

    public ObservableCollection<PluginManagerRow> Plugins { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRowSelected))]
    [NotifyPropertyChangedFor(nameof(ToggleLabel))]
    [NotifyPropertyChangedFor(nameof(SelectedStateText))]
    [NotifyPropertyChangedFor(nameof(CanToggle))]
    public partial PluginManagerRow? SelectedPlugin { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToggleLabel))]
    [NotifyPropertyChangedFor(nameof(CanToggle))]
    public partial bool IsChangingState { get; set; }

    [ObservableProperty]
    public partial string OperationMessage { get; set; } = string.Empty;

    public bool CanToggle => IsRowSelected && !IsChangingState
        && SelectedPlugin?.State is not (PluginRuntimeState.Starting or PluginRuntimeState.Activating or PluginRuntimeState.Stopping);

    public string ToggleLabel => Resource(IsChangingState ? "Plugins.Working"
        : SelectedPlugin?.State is PluginRuntimeState.Active or PluginRuntimeState.Activating
            ? "Plugins.Disable" : "Plugins.Enable");

    public string SelectedStateText => SelectedPlugin is { } row
        ? Resource($"Plugins.State.{row.State}") : string.Empty;

    public bool IsRowSelected => SelectedPlugin is not null;

    public string InstalledDirectory => _mainViewModel.PluginSystem?.Service.Paths.InstalledRoot ?? string.Empty;

    public bool SafeStartAllPlugins
    {
        get => _mainViewModel.PluginSystem?.Service.SafeStartAllPlugins ?? false;
        set
        {
            if (_mainViewModel.PluginSystem is { } system && system.Service.SafeStartAllPlugins != value)
            {
                system.Service.SafeStartAllPlugins = value;
                OnPropertyChanged();
            }
        }
    }

    public bool HasPendingUpdates => Plugins.Any(row => row.PendingUpdate);

    public string BudgetSummary
    {
        get
        {
            PluginSystemHost? system = _mainViewModel.PluginSystem;
            if (system is null)
            {
                return string.Empty;
            }

            PluginBudgetSample? sample = system.Service.Budget.LastSample;
            if (sample is null)
            {
                return Resource("Plugins.Budget.Sampling");
            }

            return string.Format(
                Resource("Plugins.Budget.Summary"),
                sample.TotalPrivateBytes / 1_000_000d,
                system.Service.Budget.Configuration.TotalBudgetBytes / 1_000_000d,
                sample.HostPrivateBytes / 1_000_000d);
        }
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
    private async Task InstallPluginAsync()
    {
        PluginSystemHost? system = _mainViewModel.PluginSystem;
        if (system is null)
        {
            return;
        }

        OpenFileDialog dialog = new()
        {
            Filter = "DuCom plugin package (*.dcpack)|*.dcpack",
            CheckFileExists = true,
            Title = Resource("Plugins.Install.PickTitle"),
        };
        if (Application.Current?.MainWindow is { } owner && dialog.ShowDialog(owner) != true)
        {
            return;
        }
        else if (dialog.FileName.Length == 0)
        {
            return;
        }

        PackageValidationResult result;
        try
        {
            // Inspection is deliberately non-persistent: neither package files nor permissions
            // are committed until the user has reviewed this exact content digest.
            result = system.Service.InspectPack(dialog.FileName);
        }
        catch (Exception exception)
        {
            ThemedMessageDialog.Show(Application.Current?.MainWindow, exception.Message,
                Resource("Plugins.Install.RejectedTitle"), ThemedMessageDialogKind.Warning);
            return;
        }
        if (!result.Accepted)
        {
            ThemedMessageDialog.Show(
                Application.Current?.MainWindow,
                string.Join(Environment.NewLine, result.Errors),
                Resource("Plugins.Install.RejectedTitle"),
                ThemedMessageDialogKind.Warning);
            return;
        }

        string permissions = result.Manifest.Permissions.Count == 0
            ? Resource("Plugins.Install.NoPermissions")
            : string.Join(", ", result.Manifest.Permissions);
        string message = string.Format(
            Resource("Plugins.Install.Confirm"),
            result.Manifest.Id,
            result.Manifest.Version,
            result.Manifest.Name,
            permissions);
        if (!ThemedMessageDialog.Confirm(
                Application.Current?.MainWindow,
                message,
                Resource("Plugins.Install.ConfirmTitle")))
        {
            return;
        }

        bool existingPlugin = system.Service.Registry.Current.Plugins.ContainsKey(result.Manifest.Id);
        try
        {
            if (existingPlugin)
            {
                if (!system.Service.StageUpdate(dialog.FileName, approveNewPermissions: true))
                {
                    throw new InvalidOperationException(Resource("Plugins.Install.RejectedTitle"));
                }
                OperationMessage = string.Format(Resource("Plugins.Update.Staged"), result.Manifest.Version);
                RefreshPlugins();
                return;
            }

            result = system.Service.InstallPack(dialog.FileName, result.Digest);
        }
        catch (Exception exception)
        {
            ThemedMessageDialog.Show(Application.Current?.MainWindow, exception.Message,
                Resource("Plugins.Install.RejectedTitle"), ThemedMessageDialogKind.Warning);
            return;
        }
        if (!result.Accepted)
        {
            ThemedMessageDialog.Show(Application.Current?.MainWindow, string.Join(Environment.NewLine, result.Errors),
                Resource("Plugins.Install.RejectedTitle"), ThemedMessageDialogKind.Warning);
            return;
        }

        system.Service.SetEnabled(result.Manifest.Id, true);
        _ = await system.Service.StartRegisteredAsync(result.Manifest.Id);
        RefreshPlugins();
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
                        Resource("Plugins.Retry.Title")))
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
                        Resource("Plugins.Retry.Title")))
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

    [RelayCommand]
    private async Task UninstallSelectedAsync()
    {
        if (SelectedPlugin is not { } row || _mainViewModel.PluginSystem is not { } system)
        {
            return;
        }

        if (row.Source == "BuiltIn")
        {
            ThemedMessageDialog.Show(
                Application.Current?.MainWindow,
                Resource("Plugins.Uninstall.BuiltIn"),
                Resource("Plugins.Uninstall.Title"),
                ThemedMessageDialogKind.Information);
            return;
        }

        if (!ThemedMessageDialog.Confirm(
                Application.Current?.MainWindow,
                string.Format(Resource("Plugins.Uninstall.Confirm"), row.Id),
                Resource("Plugins.Uninstall.Title")))
        {
            return;
        }

        try
        {
            await system.Service.UninstallAsync(row.Id);
        }
        catch (Exception exception)
        {
            Program.DiagnosticLog?.Error("Plugin uninstall failed; package and data were retained where possible.", exception);
            ThemedMessageDialog.Show(Application.Current?.MainWindow, exception.Message,
                Resource("Plugins.Uninstall.Title"), ThemedMessageDialogKind.Error);
        }

        RefreshPlugins();
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
                Resource("Plugins.ApplyRestart.Title")))
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

    public PluginRuntimeController? GetSelectedController() =>
        SelectedPlugin is { } row ? _mainViewModel.PluginSystem?.Service.GetOrCreateController(row.Id) : null;

    private void ScheduleRefresh() =>
        Application.Current?.Dispatcher.BeginInvoke(RefreshPlugins);

    private static string Resource(string key) => Application.Current?.TryFindResource(key) as string ?? key;
}
