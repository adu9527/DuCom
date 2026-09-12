using DuCom.Plugin;
using DuCom.PluginHost.Core;
using DuCom.PluginHost.Diagnostics;
using DuCom.PluginHost.Packages;
using DuCom.PluginHost.Registry;

namespace DuCom.PluginHost;

public sealed partial class PluginSystemService
{
    public async Task<bool> StartRegisteredAsync(string pluginId)
    {
        ArgumentException.ThrowIfNullOrEmpty(pluginId);
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_registry.Current.Plugins.TryGetValue(pluginId, out PluginRegistryEntry? entry)
                || !entry.Enabled || entry.FaultDisabled is not null || _registry.Current.SafeStartAllPlugins)
            {
                return false;
            }

            if (!_budget.CanActivatePlugins())
            {
                ProgramLog?.Invoke($"Budget warning active; '{pluginId}' activation deferred.");
                return false;
            }

            PluginRuntimeController? controller = GetOrCreateController(pluginId);
            if (controller is null)
            {
                return false;
            }

            bool started = await controller.StartAsync().ConfigureAwait(false);
            if (started)
            {
                NotifyPluginStarted(pluginId);
            }

            Changed?.Invoke();
            return started;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task StopAsync(string pluginId)
    {
        ArgumentException.ThrowIfNullOrEmpty(pluginId);
        PluginRuntimeController? controller;
        lock (_controllers)
        {
            if (!_controllers.TryGetValue(pluginId, out controller))
            {
                return;
            }

        }

        await StopControllerAsync(controller).ConfigureAwait(false);
        Changed?.Invoke();
    }

    private async Task StopControllerAsync(PluginRuntimeController controller)
    {
        MarkUserStopRequested(controller.Manifest.Id);
        try
        {
            await controller.StopAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            ProgramLog?.Invoke($"Stop failed for '{controller.Manifest.Id}': {exception.Message}");
            await controller.FaultAsync($"停止失败 / stop failure: {exception.Message}", exitConfirmed: false).ConfigureAwait(false);
        }
    }

    public async Task StopAllAsync()
    {
        List<PluginRuntimeController> controllers;
        lock (_controllers)
        {
            controllers = [.. _controllers.Values];
        }

        foreach (PluginRuntimeController controller in controllers)
        {
            await StopControllerAsync(controller).ConfigureAwait(false);
        }
    }

    private async Task HandleBudgetStopAsync(BudgetStopRequest request)
    {
        PluginRuntimeController? controller;
        lock (_controllers)
        {
            controller = _controllers.Values.FirstOrDefault(candidate => candidate.WorkerPid == request.Pid);
        }

        if (controller is not null)
        {
            ProgramLog?.Invoke($"Budget emergency: stopping '{controller.Manifest.Id}' (pid {request.Pid}, {request.PrivateBytes:N0} bytes).");
            await controller.StopForBudgetAsync(request.Sample).ConfigureAwait(false);
            Changed?.Invoke();
        }
    }

    private async Task RecoverBudgetStoppedPluginsAsync()
    {
        List<string> candidates;
        lock (_controllers)
        {
            candidates = [.. _controllers.Values
                .Where(candidate => candidate.State == PluginRuntimeState.StoppedByBudget)
                .Select(candidate => candidate.Manifest.Id)];
        }

        foreach (string pluginId in candidates)
        {
            // Re-check state per plugin: another path may have restarted or disabled it.
            PluginRuntimeController? controller = GetOrCreateController(pluginId);
            if (controller?.State != PluginRuntimeState.StoppedByBudget)
            {
                continue;
            }

            ProgramLog?.Invoke($"Budget recovered: restarting '{pluginId}'.");
            if (await StartRegisteredAsync(pluginId).ConfigureAwait(false))
            {
                Changed?.Invoke();
            }
        }
    }

    public PluginRuntimeController? GetOrCreateController(string pluginId)
    {
        PluginRuntimeController? Reject(string reason)
        {
            _startupRejections[pluginId] = reason;
            ProgramLog?.Invoke(reason);
            Changed?.Invoke();
            return null;
        }

        PluginRegistryEntry? entry = _registry.Current.Plugins.TryGetValue(pluginId, out PluginRegistryEntry? found) ? found : null;
        if (entry is null)
        {
            return null;
        }

        InstalledVersionRecord? version = entry.InstalledVersions.FirstOrDefault(v => v.Version == entry.SelectedVersion);
        if (version is null)
        {
            return Reject($"Installed package '{pluginId}' has no registered selected version '{entry.SelectedVersion}'; execution refused.");
        }

        PackageValidationResult result;
        try
        {
            PluginPackageInstaller.ValidateExtractedPackage(version.Path, _isOfficialNamespace, out result);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Reject($"Installed package '{pluginId}' could not be revalidated; execution refused: {exception.Message}");
        }
        if (!result.Accepted)
        {
            return Reject($"Installed package '{pluginId}' failed revalidation: {string.Join("; ", result.Errors)}");
        }

        if (!string.Equals(result.Manifest.Id, pluginId, StringComparison.Ordinal)
            || !string.Equals(result.Manifest.Version, version.Version, StringComparison.Ordinal)
            || !string.Equals(result.Digest, version.Digest, StringComparison.OrdinalIgnoreCase))
        {
            return Reject($"Installed package '{pluginId}' no longer matches its registered identity or digest; execution refused. Registered={pluginId}/{version.Version}, digest={version.Digest}; actual={result.Manifest.Id}/{result.Manifest.Version}, digest={result.Digest}.");
        }

        if (!IsRuntimeCompatible(result.Manifest, out string? incompatibility))
        {
            return Reject($"Installed package '{pluginId}' is incompatible: {incompatibility}");
        }

        IReadOnlyList<string> grantedPermissions = [.. result.Manifest.Permissions
            .Where(permission => entry.ApprovedPermissions.Contains(permission, StringComparer.Ordinal))];
        if (grantedPermissions.Count != result.Manifest.Permissions.Count)
        {
            return Reject($"Plugin '{pluginId}' requests permissions that have not been approved; execution refused.");
        }

        _startupRejections.TryRemove(pluginId, out _);
        lock (_controllers)
        {
            if (_controllers.TryGetValue(pluginId, out PluginRuntimeController? existing)
                && string.Equals(existing.PackageDigest, result.Digest, StringComparison.OrdinalIgnoreCase))
            {
                return existing;
            }
        }

        PluginRuntimeController controller = new(
            result.Manifest,
            version.Path,
            result.Digest,
            _paths,
            _launcher,
            _environment,
            new PluginLimits { ProcessMemoryLimitBytes = _budget.Configuration.PerPluginQuotaBytes },
            new PluginDiagnosticsLog(_paths.GetDiagnosticsFilePath(pluginId), pluginId),
            _budget,
            _registry,
            grantedPermissions,
            _hostTempDiskBudget,
            _hostTempLedger);
        controller.StateChanged += (_, _) => Changed?.Invoke();
        controller.StateChanged += (_, change) => OnControllerFaultDisabled(change);
        controller.FaultNotice += async notice =>
        {
            try
            {
                await HandleFaultNoticeAsync(notice).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                ProgramLog?.Invoke($"Fault notice handling for '{notice.PluginId}' failed: {exception.Message}");
            }
        };
        lock (_controllers)
        {
            _controllers[pluginId] = controller;
        }

        return controller;
    }

    private bool IsRuntimeCompatible(PluginManifest manifest, out string? reason)
    {
        const int supportedProtocolMajor = 1;
        const int supportedProtocolMinor = 0;
        if (PluginManifestValidator.ParseProtocolVersion(manifest.ProtocolVersion) is not (int major, int minor)
            || major != supportedProtocolMajor || minor > supportedProtocolMinor)
        {
            reason = $"protocol {manifest.ProtocolVersion} is not supported by DPP/1.0.";
            return false;
        }

        if (!PluginManifestValidator.TryCompareSemVer(_environment.HostVersion, manifest.MinHostVersion, out int hostVersionComparison)
            || hostVersionComparison < 0)
        {
            reason = $"host {_environment.HostVersion} is below minHostVersion {manifest.MinHostVersion}.";
            return false;
        }

        if (!string.Equals(manifest.Runtime.Framework, "net10.0", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(manifest.Runtime.Rid, "win-x64", StringComparison.OrdinalIgnoreCase))
        {
            reason = $"runtime {manifest.Runtime.Framework}/{manifest.Runtime.Rid} is not supported.";
            return false;
        }

        reason = null;
        return true;
    }

    public IReadOnlyList<PluginManagerRow> BuildManagerRows()
    {
        List<PluginManagerRow> rows = [];
        foreach ((string id, PluginRegistryEntry entry) in _registry.Current.Plugins)
        {
            PluginRuntimeController? controller = null;
            lock (_controllers)
            {
                _controllers.TryGetValue(id, out controller);
            }

            InstalledVersionRecord? version = entry.InstalledVersions.FirstOrDefault(v => v.Version == entry.SelectedVersion);
            _startupRejections.TryGetValue(id, out string? rejection);
            bool running = controller?.State is PluginRuntimeState.Starting or PluginRuntimeState.Activating or PluginRuntimeState.Active or PluginRuntimeState.Stopping;
            rows.Add(new PluginManagerRow
            {
                Id = id,
                Name = controller?.Manifest.Name ?? GetManifestName(version?.Path) ?? id,
                Version = entry.SelectedVersion,
                Source = entry.BuiltIn ? "BuiltIn" : version?.Source ?? "-",
                State = running ? controller!.State : entry.FaultDisabled is not null ? PluginRuntimeState.FaultDisabled
                    : rejection is not null ? PluginRuntimeState.Rejected : controller?.State ?? PluginRuntimeState.Disabled,
                Capabilities = controller?.Manifest.Capabilities ?? [],
                Permissions = controller?.Manifest.Permissions ?? [],
                FaultReason = entry.FaultDisabled?.Reason ?? rejection ?? string.Empty,
                PendingUpdate = entry.PendingUpdate is not null,
                PendingUpdateVersion = entry.PendingUpdate?.Version ?? string.Empty,
                WorkerPid = controller?.WorkerPid ?? 0,
                WorkerPrivateBytes = controller?.WorkerPrivateBytes ?? 0,
                Enabled = entry.Enabled,
            });
        }

        return rows;
    }

    private static string? GetManifestName(string? packagePath)
    {
        if (string.IsNullOrWhiteSpace(packagePath)) return null;
        try
        {
            PluginPackageInstaller.ValidateExtractedPackage(packagePath, _ => true, out PackageValidationResult result);
            return result.Accepted ? result.Manifest.Name : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public IReadOnlyList<(DateTimeOffset Timestamp, PluginLogLevel Level, string Message)> GetDiagnostics(string pluginId)
    {
        ArgumentException.ThrowIfNullOrEmpty(pluginId);
        lock (_controllers)
        {
            if (_controllers.TryGetValue(pluginId, out PluginRuntimeController? controller))
            {
                return controller.DiagnosticsSnapshot;
            }
        }

        return [];
    }
}
