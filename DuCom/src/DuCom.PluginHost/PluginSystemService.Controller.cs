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
            return await StartRegisteredLockedAsync(pluginId).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task<bool> StartRegisteredLockedAsync(string pluginId)
    {
        if (Volatile.Read(ref _autoRestartShutdown) != 0
            || !_registry.Current.Plugins.TryGetValue(pluginId, out PluginRegistryEntry? entry)
            || !entry.Enabled || entry.FaultDisabled is not null || _registry.Current.SafeStartAllPlugins)
        {
            return false;
        }

        if (!_budget.CanActivatePlugins(pluginId))
        {
            ProgramLog?.Invoke(
                $"Tool memory budget exhausted for '{pluginId}' group; activation deferred.");
            return false;
        }

        PluginRuntimeController? controller = GetOrCreateController(pluginId);
        if (controller is null)
        {
            return false;
        }

        if (controller.State is PluginRuntimeState.Active or PluginRuntimeState.Starting or PluginRuntimeState.Activating)
            return false;
        string digest = controller.PackageDigest;
        bool approved = await AuthorizeNativeHelpersAsync(controller.Manifest, digest, _autoRestartCancellation.Token).ConfigureAwait(false);
        if (!approved || Volatile.Read(ref _autoRestartShutdown) != 0
            || !_registry.Current.Plugins.TryGetValue(pluginId, out entry) || !entry.Enabled
            || entry.FaultDisabled is not null || _registry.Current.SafeStartAllPlugins)
            return false;
        controller = GetOrCreateController(pluginId);
        if (controller is null || controller.PackageDigest != digest || !_budget.CanActivatePlugins(pluginId)) return false;

        bool started = await controller.StartAsync(nativeHelpersApproved: approved).ConfigureAwait(false);
        if (started)
        {
            NotifyPluginStarted(pluginId);
        }

        Changed?.Invoke();
        return started;
    }

    internal async Task<bool> AuthorizeNativeHelpersAsync(PluginManifest manifest, string packageDigest, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (manifest.NativeHelpers.Count == 0 || !manifest.Permissions.Contains(Permission.NativeHelpersExecute))
            return true;
        string key = manifest.Id + ":" + packageDigest;
        if (_nativeHelperApprovals.ContainsKey(key)) return true;
        // Consent is remembered per exact package: a restart stays silent, but a
        // package update (different digest) asks once again.
        if (_registry.Current.Plugins.TryGetValue(manifest.Id, out PluginRegistryEntry? entry)
            && string.Equals(entry.TrustedNativeHelperDigest, packageDigest, StringComparison.OrdinalIgnoreCase))
        {
            _nativeHelperApprovals.TryAdd(key, 0);
            return true;
        }
        bool approved = await _environment.ConfirmNativeHelperExecutionAsync(new HostNativeHelperConsent
        {
            PluginId = manifest.Id,
            PluginName = manifest.Name,
            Version = manifest.Version,
            PackageDigest = packageDigest,
            HelperEntryPoints = [.. manifest.NativeHelpers.Select(helper => helper.EntryPoint)],
        }, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (approved)
        {
            _nativeHelperApprovals.TryAdd(key, 0);
            try
            {
                _registry.Mutate(data =>
                {
                    if (data.Plugins.TryGetValue(manifest.Id, out PluginRegistryEntry? current))
                    {
                        data.Plugins[manifest.Id] = current with { TrustedNativeHelperDigest = packageDigest };
                    }
                    return data;
                });
                ProgramLog?.Invoke($"Native-helper trust remembered for '{manifest.Id}' (digest {packageDigest[..8]}…); future starts of this exact package are silent.");
            }
            catch (Exception exception)
            {
                // Persisting is an optimization; the in-process cache still applies.
                ProgramLog?.Invoke($"Could not persist native-helper trust for '{manifest.Id}': {exception.Message}");
            }
        }
        return approved;
    }

    public async Task StopAsync(string pluginId)
    {
        ArgumentException.ThrowIfNullOrEmpty(pluginId);
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopLockedAsync(pluginId).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task StopLockedAsync(string pluginId)
    {
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
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopAllLockedAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task StopAllLockedAsync()
    {
        List<PluginRuntimeController> controllers;
        lock (_controllers)
        {
            controllers = [.. _controllers.Values];
        }

        // Stop every worker concurrently: a serial loop spends up to one full
        // deactivate-grace + terminate-wait budget PER plugin (8s with defaults),
        // which overrun the 15s shutdown watchdog when several plugins are active.
        // Each controller stops its own worker independently; FaultAsync on one
        // does not affect the others' teardown.
        await Task.WhenAll(controllers.Select(StopControllerAsync)).ConfigureAwait(false);
    }

    private async Task HandleBudgetStopAsync(BudgetStopRequest request)
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await HandleBudgetStopLockedAsync(request).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    internal static bool MatchesBudgetStop(BudgetStopRequest request, string pluginId, uint workerPid, string activationId) =>
        workerPid != 0 && request.Pid == workerPid
        && string.Equals(request.PluginId, pluginId, StringComparison.Ordinal)
        && request.ActivationId.Length > 0
        && string.Equals(request.ActivationId, activationId, StringComparison.Ordinal);

    private async Task HandleBudgetStopLockedAsync(BudgetStopRequest request)
    {
        PluginRuntimeController? controller;
        lock (_controllers)
        {
            controller = _controllers.GetValueOrDefault(request.PluginId);
            if (controller is not null && !MatchesBudgetStop(request, controller.Manifest.Id, controller.WorkerPid, controller.ActivationId))
                controller = null;
        }

        if (controller is not null)
        {
            _budgetRecoveryPending.Add(controller.Manifest.Id);
            ProgramLog?.Invoke(
                $"Budget emergency: stopping '{controller.Manifest.Id}' " +
                $"(pid {request.Pid}, {request.PrivateBytes:N0} bytes, reason={request.Reason}, " +
                $"growth={request.GrowthBytes:N0}, host={request.Sample.HostPrivateBytes:N0}, " +
                $"bes={request.Sample.BesPluginBytes:N0}, standard={request.Sample.StandardPluginBytes:N0}, " +
                $"total={request.Sample.TotalPrivateBytes:N0}).");
            await controller.StopForBudgetAsync(request.Sample).ConfigureAwait(false);
            Changed?.Invoke();
            if (_budget.CanRecoverPlugins())
            {
                ScheduleBudgetRecovery();
            }
        }
    }

    private int _budgetRecoveryScheduled;

    // Bring stopped plugins back one at a time instead of refilling the whole pool within two
    // sample ticks; the point of the stop is to leave headroom, not to oscillate around it.
    private const int BudgetRecoveryCooldownSeconds = 5;
    private long _nextBudgetRecoveryTimestamp;

    private void OnBudgetSampled(PluginBudgetSample sample)
    {
        LogMemorySample(sample, System.Diagnostics.Stopwatch.GetTimestamp());
        if (sample.TotalPrivateBytes < _budget.Configuration.WarningThresholdBytes)
        {
            ScheduleBudgetRecovery();
        }
    }

    private void ScheduleBudgetRecovery()
    {
        if (Interlocked.CompareExchange(ref _budgetRecoveryScheduled, 1, 0) != 0)
        {
            return;
        }
        _ = Task.Run(RecoverOneBudgetStoppedPluginAsync);
    }

    private async Task RecoverOneBudgetStoppedPluginAsync()
    {
        try
        {
            await _lifecycleGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!_budget.CanRecoverPlugins())
                {
                    return;
                }

                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                if (now < Volatile.Read(ref _nextBudgetRecoveryTimestamp))
                {
                    return;
                }

                await TryRecoverBudgetQueueAsync(_budgetRecoveryPending.Order(StringComparer.Ordinal).ToArray(), async pluginId =>
                {
                    PluginRegistryEntry? entry = _registry.Current.Plugins.TryGetValue(pluginId, out PluginRegistryEntry? found) ? found : null;
                    if (entry is null || !entry.Enabled)
                    {
                        _budgetRecoveryPending.Remove(pluginId);
                        return false;
                    }

                    PluginRuntimeController? controller = GetOrCreateController(pluginId);
                    if (controller is null
                        || controller.State is PluginRuntimeState.FaultDisabled
                            or PluginRuntimeState.Disabled
                            or PluginRuntimeState.Rejected
                            or PluginRuntimeState.Active
                            or PluginRuntimeState.Activating)
                    {
                        // The plugin was uninstalled, refused, turned off by the user, or is
                        // already running again: nothing left to recover for this entry.
                        _budgetRecoveryPending.Remove(pluginId);
                        return false;
                    }
                    if (controller.State != PluginRuntimeState.StoppedByBudget)
                    {
                        return false;
                    }

                    ProgramLog?.Invoke($"Budget recovered: restarting '{pluginId}'.");
                    if (await StartRegisteredLockedAsync(pluginId).ConfigureAwait(false))
                    {
                        _budgetRecoveryPending.Remove(pluginId);
                        Volatile.Write(
                            ref _nextBudgetRecoveryTimestamp,
                            System.Diagnostics.Stopwatch.GetTimestamp()
                                + (long)(BudgetRecoveryCooldownSeconds * System.Diagnostics.Stopwatch.Frequency));
                        Changed?.Invoke();
                        return true;
                    }
                    // A blocked group must not starve another independently admissible group.
                    return false;
                }).ConfigureAwait(false);
            }
            finally
            {
                _lifecycleGate.Release();
            }
        }
        finally
        {
            Interlocked.Exchange(ref _budgetRecoveryScheduled, 0);
        }
    }

    internal static async Task<bool> TryRecoverBudgetQueueAsync(IEnumerable<string> pluginIds, Func<string, Task<bool>> tryRecover)
    {
        foreach (string pluginId in pluginIds)
            if (await tryRecover(pluginId).ConfigureAwait(false)) return true;
        return false;
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
            new PluginLimits { ProcessMemoryLimitBytes = _budget.GetPluginQuota(pluginId) },
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
                // Protective budget stops are not faults. Persisting them would clear a real
                // pending notice for the same plugin and make the manager look like the plugin
                // crashed, so they only get a diagnostic line and a state refresh.
                if (notice.BudgetProtective)
                {
                    ProgramLog?.Invoke(
                        $"Plugin '{notice.PluginId}' stopped to protect the tool-wide memory budget; no fault recorded.");
                    return;
                }

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
