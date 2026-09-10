using DuCom.PluginHost.Core;
using DuCom.PluginHost.Packages;
using DuCom.PluginHost.Registry;

namespace DuCom.PluginHost;

public sealed partial class PluginSystemService
{
    public PackageValidationResult InspectPack(string dcpackPath) => _installer.InspectDcPack(dcpackPath, _isOfficialNamespace);

    public PackageValidationResult InstallPack(string dcpackPath, string? expectedDigest = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(dcpackPath);
        PackageValidationResult result = _installer.InstallDcPack(dcpackPath, _isOfficialNamespace, expectedDigest);
        if (result.Accepted)
        {
            _registry.Mutate(data =>
            {
                PluginRegistryEntry entry = data.Plugins.TryGetValue(result.Manifest.Id, out PluginRegistryEntry? existing)
                    ? existing
                    : new PluginRegistryEntry { Id = result.Manifest.Id };
                List<InstalledVersionRecord> versions = [.. entry.InstalledVersions.Where(v => v.Version != result.Manifest.Version)];
                versions.Add(new InstalledVersionRecord
                {
                    Version = result.Manifest.Version,
                    Path = _installer.GetVersionDirectory(result.Manifest.Id, result.Manifest.Version),
                    Digest = result.Digest,
                    InstalledAtUtc = DateTime.UtcNow,
                    Source = InstalledVersionRecord.SourceDcPack,
                });
                data.Plugins[result.Manifest.Id] = entry with
                {
                    InstalledVersions = versions,
                    Enabled = entry.InstalledVersions.Count == 0 ? false : entry.Enabled,
                    SelectedVersion = string.IsNullOrEmpty(entry.SelectedVersion) ? result.Manifest.Version : entry.SelectedVersion,
                // Installation confirmation establishes grants only for a first install. Do not
                // treat an intentionally empty approved set as an uninitialized record.
                ApprovedPermissions = entry.InstalledVersions.Count == 0 ? [.. result.Manifest.Permissions] : entry.ApprovedPermissions,
                };
            });
            Changed?.Invoke();
        }

        return result;
    }

    public bool StageUpdate(string dcpackPath, bool approveNewPermissions = false)
    {
        PackageValidationResult result = InstallPack(dcpackPath);
        if (!result.Accepted)
        {
            return false;
        }

        _registry.Mutate(data =>
        {
            if (!data.Plugins.TryGetValue(result.Manifest.Id, out PluginRegistryEntry? entry))
            {
                return data;
            }

            if (string.Equals(entry.SelectedVersion, result.Manifest.Version, StringComparison.Ordinal))
            {
                return data;
            }

            IReadOnlyList<string> newPermissions = [.. result.Manifest.Permissions.Where(p => !entry.ApprovedPermissions.Contains(p, StringComparer.Ordinal))];
            data.Plugins[result.Manifest.Id] = entry with
            {
                ApprovedPermissions = approveNewPermissions
                    ? [.. entry.ApprovedPermissions.Union(newPermissions, StringComparer.Ordinal)]
                    : entry.ApprovedPermissions,
                PendingUpdate = new PendingUpdateRecord
                {
                    Version = result.Manifest.Version,
                    Digest = result.Digest,
                    PreviousVersion = entry.SelectedVersion,
                    RequiresPermissionConsent = newPermissions.Count > 0 && !approveNewPermissions,
                    NewPermissions = newPermissions,
                },
            };

            return data;
        });
        Changed?.Invoke();
        return true;
    }

    public IReadOnlyList<string> ApplyPendingUpdates()
    {
        // The switch is deliberately performed during the next startup, after the stopped
        // application has persisted its intent and the target package can be revalidated.
        Changed?.Invoke();
        return [];
    }

    private static bool IsBuiltIn(InstalledVersionRecord record) => string.Equals(record.Source, InstalledVersionRecord.SourceBuiltIn, StringComparison.Ordinal);

    private static IEnumerable<string> GetAllPermissionsForVersion(PluginRegistryEntry entry, string version) =>
        entry.InstalledVersions.Where(v => v.Version == version).SelectMany(_ => Array.Empty<string>()).Concat(entry.ApprovedPermissions);

    public void SetEnabled(string pluginId, bool enabled, bool stopImmediately = true)
    {
        ArgumentException.ThrowIfNullOrEmpty(pluginId);
        _registry.Mutate(data =>
        {
            if (data.Plugins.TryGetValue(pluginId, out PluginRegistryEntry? entry))
            {
                data.Plugins[pluginId] = entry with { Enabled = enabled };
            }
        });

        if (!enabled && stopImmediately)
        {
            _ = StopAsync(pluginId);
        }

        Changed?.Invoke();
    }

    public void ClearFaultDisable(string pluginId)
    {
        ArgumentException.ThrowIfNullOrEmpty(pluginId);
        _registry.Mutate(data =>
        {
            if (data.Plugins.TryGetValue(pluginId, out PluginRegistryEntry? entry))
            {
                data.Plugins[pluginId] = entry with { FaultDisabled = null };
            }
        });
        Changed?.Invoke();
    }

    public void Uninstall(string pluginId)
    {
        ArgumentException.ThrowIfNullOrEmpty(pluginId);
        lock (_controllers)
        {
            if (_controllers.TryGetValue(pluginId, out PluginRuntimeController? controller)
                && controller.State is PluginRuntimeState.Starting or PluginRuntimeState.Activating or PluginRuntimeState.Active or PluginRuntimeState.Stopping)
            {
                throw new InvalidOperationException("Use UninstallAsync to stop a running plugin before uninstalling.");
            }
        }

        if (!_lifecycleGate.Wait(0))
        {
            throw new InvalidOperationException("A plugin lifecycle operation is in progress; use UninstallAsync.");
        }
        try
        {
            UninstallStopped(pluginId);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task UninstallAsync(string pluginId)
    {
        ArgumentException.ThrowIfNullOrEmpty(pluginId);
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            PluginRuntimeController? controller;
            lock (_controllers)
            {
                _controllers.TryGetValue(pluginId, out controller);
            }

            // Keep an independent process handle: the controller currently discards its wait result.
            uint pid = controller?.WorkerPid ?? 0;
            using System.Diagnostics.Process? process = pid > 0
                ? System.Diagnostics.Process.GetProcessById(checked((int)pid)) : null;
            if (process is not null)
            {
                _ = process.Handle;
            }
            await StopAsync(pluginId).ConfigureAwait(false);
            if (process is not null)
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            UninstallStopped(pluginId, process?.HasExited == true);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private void UninstallStopped(string pluginId, bool exitConfirmed = false)
    {
        if (!_registry.Current.Plugins.TryGetValue(pluginId, out PluginRegistryEntry? entry))
        {
            return;
        }
        lock (_controllers)
        {
            if (_controllers.TryGetValue(pluginId, out PluginRuntimeController? controller)
                && (controller.WorkerPid != 0 || controller.State is PluginRuntimeState.Starting or PluginRuntimeState.Activating or PluginRuntimeState.Active or PluginRuntimeState.Stopping))
            {
                throw new InvalidOperationException("Plugin exit has not been confirmed; package retained.");
            }
            if (!exitConfirmed && (entry.FaultDisabled is { ExitConfirmed: false }
                || entry.LastAttempt is { EndedCleanly: null or false }
                    && !(entry.FaultDisabled is { ExitConfirmed: true } fault && fault.ActivationId == entry.LastAttempt.ActivationId)
                || controller is not null && controller.ActivationId.Length != 0))
            {
                throw new InvalidOperationException("Plugin exit has not been confirmed; package retained.");
            }

            foreach (InstalledVersionRecord version in entry.InstalledVersions)
            {
                if (Directory.Exists(version.Path))
                {
                    Directory.Delete(version.Path, recursive: true);
                }
            }
            _registry.Mutate(data => { data.Plugins.Remove(pluginId); });
            _controllers.Remove(pluginId);
            _startupRejections.TryRemove(pluginId, out _);
        }
        Changed?.Invoke();
    }
}
