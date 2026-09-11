using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DuCom.PluginHost;
using DuCom.PluginHost.Core;
using DuCom.Services.Plugins;

namespace DuCom.ViewModels;

public partial class PluginManagerViewModel
{
    public ObservableCollection<PluginManagerRow> Plugins { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRowSelected))]
    [NotifyPropertyChangedFor(nameof(ToggleLabel))]
    [NotifyPropertyChangedFor(nameof(SelectedStateText))]
    [NotifyPropertyChangedFor(nameof(CanToggle))]
    [NotifyPropertyChangedFor(nameof(CanUninstall))]
    public partial PluginManagerRow? SelectedPlugin { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToggleLabel))]
    [NotifyPropertyChangedFor(nameof(CanToggle))]
    [NotifyPropertyChangedFor(nameof(CanUninstall))]
    public partial bool IsChangingState { get; set; }

    [ObservableProperty]
    public partial string OperationMessage { get; set; } = string.Empty;

    public bool CanToggle => IsRowSelected && !IsChangingState
        && SelectedPlugin?.State is not (PluginRuntimeState.Starting or PluginRuntimeState.Activating or PluginRuntimeState.Stopping);

    public bool CanUninstall => CanToggle && SelectedPlugin?.IsBuiltIn == false;

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

    public PluginRuntimeController? GetSelectedController() =>
        SelectedPlugin is { } row ? _mainViewModel.PluginSystem?.Service.GetOrCreateController(row.Id) : null;
}
