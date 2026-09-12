using System.Windows;
using CommunityToolkit.Mvvm.Input;
using DuCom.PluginHost;
using DuCom.PluginHost.Packages;
using DuCom.Services.Plugins;
using Microsoft.Win32;

namespace DuCom.ViewModels;

public partial class PluginManagerViewModel
{
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
        bool existingPlugin = system.Service.Registry.Current.Plugins.ContainsKey(result.Manifest.Id);
        DuCom.PluginHost.Registry.PluginRegistryEntry? existingEntry = existingPlugin
            ? system.Service.Registry.Current.Plugins[result.Manifest.Id]
            : null;
        bool sameSelectedVersion = string.Equals(existingEntry?.SelectedVersion, result.Manifest.Version, StringComparison.Ordinal);
        IReadOnlyList<string> newPermissions = existingEntry is null
            ? []
            : [.. result.Manifest.Permissions.Where(permission =>
                !existingEntry.ApprovedPermissions.Contains(permission, StringComparer.Ordinal))];
        if (sameSelectedVersion && existingEntry!.InstalledVersions.Any(version =>
                string.Equals(version.Version, result.Manifest.Version, StringComparison.Ordinal) &&
                string.Equals(version.Digest, result.Digest, StringComparison.OrdinalIgnoreCase)))
        {
            OperationMessage = Resource("Plugins.Install.AlreadyInstalled");
            return;
        }
        if (sameSelectedVersion && newPermissions.Count > 0)
        {
            ThemedMessageDialog.Show(
                Application.Current?.MainWindow,
                string.Format(Resource("Plugins.Install.SameVersionNewPermissions"), string.Join(", ", newPermissions)),
                Resource("Plugins.Install.RejectedTitle"),
                ThemedMessageDialogKind.Warning);
            return;
        }
        if (!ThemedMessageDialog.Confirm(
                Application.Current?.MainWindow,
                message,
                Resource("Plugins.Install.ConfirmTitle"),
                existingPlugin ? "Dialog.Update" : "Plugins.Install.ConfirmAction",
                "Dialog.Cancel"))
        {
            return;
        }

        try
        {
            if (existingPlugin)
            {
                if (sameSelectedVersion)
                {
                    result = await system.Service.ReplaceSameVersionAsync(dialog.FileName, result.Digest);
                    OperationMessage = Resource("Plugins.Install.Replaced");
                    RefreshPlugins();
                    return;
                }
                if (!system.Service.StageUpdate(dialog.FileName, result.Digest, approveNewPermissions: true))
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
                Resource("Plugins.Uninstall.Title"),
                "Dialog.Uninstall",
                "Dialog.Cancel"))
        {
            return;
        }

        try
        {
            IsChangingState = true;
            await system.Service.UninstallAsync(row.Id);
            OperationMessage = Resource("Plugins.Uninstall.Completed");
        }
        catch (Exception exception)
        {
            Program.DiagnosticLog?.Error("Plugin uninstall failed; package and data were retained where possible.", exception);
            ThemedMessageDialog.Show(Application.Current?.MainWindow, exception.Message,
                Resource("Plugins.Uninstall.Title"), ThemedMessageDialogKind.Error);
        }
        finally
        {
            IsChangingState = false;
        }

        RefreshPlugins();
    }
}
