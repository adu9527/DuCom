using System.Windows;
using CommunityToolkit.Mvvm.Input;
using DuCom.PluginHost;
using DuCom.PluginHost.Core;
using DuCom.Services.Plugins;

namespace DuCom.ViewModels;

public partial class MainViewModel
{
    public PluginManagerViewModel PluginManager { get; }

    public Services.Plugins.PluginSystemHost? PluginSystem { get; private set; }

    public event Action? PluginMenuChanged;

    public bool IsLogPackagePluginActive => PluginSystem?.Service.BuildManagerRows().Any(row =>
        row.Id == "com.ducom.log-package" && row.State == PluginRuntimeState.Active) == true;

    internal void AttachPluginSystem(Services.Plugins.PluginSystemHost pluginSystem)
    {
        ArgumentNullException.ThrowIfNull(pluginSystem);
        PluginSystem = pluginSystem;
        OnPropertyChanged(nameof(PluginSystem));
        PluginManager.AttachPluginSystem(pluginSystem);
        pluginSystem.Ui.Changed += (_, _) =>
        {
            OnPropertyChanged(nameof(IsLogPackagePluginActive));
            PluginMenuChanged?.Invoke();
        };
        pluginSystem.Service.Changed += () => Application.Current?.Dispatcher.BeginInvoke(() =>
            OnPropertyChanged(nameof(IsLogPackagePluginActive)));
        PluginMenuChanged?.Invoke();
    }

    public IReadOnlyList<Services.Plugins.PluginMenuEntry> BuildPluginMenuEntries() =>
        PluginSystem?.Ui.BuildMenuEntries() ?? [];

    public async void InvokePluginMenu(Services.Plugins.PluginMenuEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        PluginSystemHost? system = PluginSystem;
        if (system is null)
        {
            return;
        }

        if (entry.PageId is { Length: > 0 })
        {
            // Let the plugin refresh page state (e.g. prefill the reproduction time) before the window opens.
            if (entry.CommandId is { Length: > 0 })
            {
                try
                {
                    await InvokePluginCommandAsync(entry.PluginId, entry.CommandId, values: new Dictionary<string, string>());
                }
                catch (Exception exception)
                {
                    Program.DiagnosticLog?.Warning($"Plugin menu command '{entry.CommandId}' failed: {exception.Message}");
                }
            }

            system.Ui.OpenToolPage(entry.PluginId, entry.PageId, (commandId, values) => InvokePluginCommandAsync(entry.PluginId, commandId, values));
            return;
        }

        _ = InvokePluginCommandAsync(entry.PluginId, entry.CommandId, values: new Dictionary<string, string>());
    }

    private async Task<bool> InvokePluginCommandAsync(string pluginId, string commandId, IReadOnlyDictionary<string, string> values)
    {
        PluginRuntimeController? controller = PluginSystem?.Service.GetOrCreateController(pluginId);
        if (controller is null || !controller.IsActive)
        {
            StatusMessage = GetResourceString("Plugins.NotActive");
            return false;
        }

        return string.Equals(commandId, "stop", StringComparison.Ordinal)
            ? await controller.NotifyPriorityCommandAsync(commandId, values)
            : await controller.InvokeCommandAsync(commandId, values);
    }

    internal Task<bool> InvokePluginCommandForManagerAsync(string pluginId, string commandId, IReadOnlyDictionary<string, string> values) =>
        InvokePluginCommandAsync(pluginId, commandId, values);

    [RelayCommand]
    private async Task ShowLogPackageAsync()
    {
        PluginSystemHost? system = PluginSystem;
        PluginPublishedActivation? activation = system?.Ui.Activations.FirstOrDefault(candidate => candidate.PluginId == "com.ducom.log-package");
        if (system is null || activation is null)
        {
            StatusMessage = GetResourceString("Plugins.NotActive");
            return;
        }

        string pageId = activation.ToolPages.Count > 0 ? activation.ToolPages[0].ContributionId : "packager";
        // Prefill the reproduction time with "now" (legacy behavior) right before the window opens.
        try
        {
            await InvokePluginCommandAsync(activation.PluginId, "open", values: new Dictionary<string, string>());
        }
        catch (Exception exception)
        {
            Program.DiagnosticLog?.Warning($"Log package open command failed: {exception.Message}");
        }

        system.Ui.OpenToolPage(activation.PluginId, pageId, (commandId, values) => InvokePluginCommandAsync(activation.PluginId, commandId, values));
    }

    [RelayCommand]
    private void ShowPluginManager() => OpenSettingsCategory(4);
}
