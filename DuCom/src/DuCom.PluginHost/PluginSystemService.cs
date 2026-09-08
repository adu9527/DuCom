using System.Text.Json;
using DuCom.Plugin;
using DuCom.PluginHost.Core;
using DuCom.PluginHost.Diagnostics;
using DuCom.PluginHost.Packages;
using DuCom.PluginHost.Registry;
using DuCom.PluginHost.Security;

namespace DuCom.PluginHost;

public sealed record FactoryPackFile(string RelativePath, Func<byte[]> Content);

public sealed record FactoryPackDefinition
{
    public required string PluginId { get; init; }
    public required string Version { get; init; }
    public required List<FactoryPackFile> Files { get; init; }
}

public sealed record PluginManagerRow
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Version { get; init; }
    public required string Source { get; init; }
    public required PluginRuntimeState State { get; init; }
    public required IReadOnlyList<string> Capabilities { get; init; }
    public required IReadOnlyList<string> Permissions { get; init; }
    public required string FaultReason { get; init; }
    public required bool PendingUpdate { get; init; }
    public required string PendingUpdateVersion { get; init; }
    public required uint WorkerPid { get; init; }
    public required long WorkerPrivateBytes { get; init; }
    public required bool Enabled { get; init; }

    public bool HasFaultReason => !string.IsNullOrWhiteSpace(FaultReason);

    public bool CanRetry => State is PluginRuntimeState.FaultDisabled or PluginRuntimeState.Rejected;

    public string ToggleLabel => State is PluginRuntimeState.Active or PluginRuntimeState.Activating ? "停用 / Disable" : "启用 / Enable";

    public bool IsActive => State is PluginRuntimeState.Active or PluginRuntimeState.Activating;

    public bool IsInactive => !IsActive;

    public bool IsBuiltIn => string.Equals(Source, "BuiltIn", StringComparison.Ordinal);

    public string Description => Id switch
    {
        "com.ducom.background-image" => "在 DuCom 主窗口底层显示自定义图片，支持单图和目录轮播。",
        "com.ducom.log-package" => "选择当前串口会话日志，填写问题信息并生成 ZIP 压缩包。",
        _ => "通过 DuCom 插件运行时提供扩展功能。",
    };
}

/// <summary>
/// Top-level plugin runtime facade: registry-backed discovery, factory pack materialization,
/// installation, enable/disable/retry/uninstall, worker lifecycle, and budget governance.
/// The host application owns the environment services; this type never touches UI types.
/// </summary>
public sealed class PluginSystemService : IAsyncDisposable
{
    private readonly PluginHostPaths _paths;
    private readonly IPluginHostEnvironment _environment;
    private readonly SandboxLauncher _launcher;
    private readonly PluginRegistryStore _registry;
    private readonly PluginPackageInstaller _installer;
    private readonly BudgetGovernor _budget;
    private readonly HostTempDiskBudget _hostTempDiskBudget;
    private readonly HostTempResourceLedger _hostTempLedger;
    private readonly Dictionary<string, PluginRuntimeController> _controllers = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _startupRejections = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly string _hostExecutablePath;
    private readonly Func<string, bool> _isOfficialNamespace;
    private bool _initialized;

    public PluginSystemService(
        PluginHostPaths paths,
        IPluginHostEnvironment environment,
        string hostExecutablePath,
        BudgetGovernorConfig? budgetConfig = null,
        Func<string, bool>? isOfficialNamespace = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        ArgumentException.ThrowIfNullOrEmpty(hostExecutablePath);
        _hostExecutablePath = hostExecutablePath;
        _launcher = new SandboxLauncher(hostExecutablePath);
        _registry = new PluginRegistryStore(paths.RegistryPath);
        _installer = new PluginPackageInstaller(paths.InstalledRoot);
        _budget = new BudgetGovernor(budgetConfig ?? new BudgetGovernorConfig());
        _hostTempDiskBudget = new HostTempDiskBudget(_budget.Configuration.HostTempDiskQuotaBytes);
        _hostTempLedger = new HostTempResourceLedger(paths.TempRoot, _registry.HostRunId, _hostTempDiskBudget);
        _isOfficialNamespace = isOfficialNamespace ?? (id => id.StartsWith("com.ducom.", StringComparison.Ordinal) && FactoryIds.Contains(id));
        _budget.Warning += sample => ProgramLog?.Invoke($"Plugin budget warning: total={sample.TotalPrivateBytes:N0} host={sample.HostPrivateBytes:N0}");
        _budget.EmergencyStop += request => _ = HandleBudgetStopAsync(request);
        _budget.Recovered += sample => ProgramLog?.Invoke($"Plugin budget recovered: total={sample.TotalPrivateBytes:N0}");
    }

    public static readonly IReadOnlyList<string> FactoryIds =
    [
        "com.ducom.background-image",
        "com.ducom.log-package",
    ];

    public event Action<string>? ProgramLog;

    public event Action? Changed;

    public event Action<HostFaultNotice>? FaultNotice;

    public PluginRegistryStore Registry => _registry;

    public BudgetGovernor Budget => _budget;

    public PluginHostPaths Paths => _paths;

    public string DiagnosticsRoot => _paths.DiagnosticsRoot;

    public bool SafeStartAllPlugins
    {
        get => _registry.Current.SafeStartAllPlugins;
        set
        {
            _registry.Mutate(data => data with { SafeStartAllPlugins = value });
            Changed?.Invoke();
        }
    }

    public async Task InitializeAsync(IReadOnlyList<FactoryPackDefinition> factoryPacks)
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        _registry.Load();
        _budget.Start();

        foreach (FactoryPackDefinition pack in factoryPacks)
        {
            MaterializeFactoryPack(pack);
        }

        ValidatePendingUpdatesBeforeStartup();

        RecoverStartupAttempts();

        foreach (PluginRegistryEntry entry in _registry.Current.Plugins.Values.Where(entry => entry.Enabled).ToList())
        {
            if (_registry.Current.SafeStartAllPlugins)
            {
                ProgramLog?.Invoke($"Safe start is active; '{entry.Id}' stays stopped this run.");
                continue;
            }

            if (entry.FaultDisabled is not null)
            {
                continue;
            }

            await StartRegisteredAsync(entry.Id).ConfigureAwait(false);
        }

        await ConsumePendingNoticesAsync().ConfigureAwait(false);
        Changed?.Invoke();
    }

    private void ValidatePendingUpdatesBeforeStartup()
    {
        _registry.Mutate(data =>
        {
            foreach ((string id, PluginRegistryEntry entry) in data.Plugins.ToList())
            {
                if (entry.PendingUpdate is not { RequiresPermissionConsent: false } pending) continue;
                InstalledVersionRecord? target = entry.InstalledVersions.FirstOrDefault(version => version.Version == pending.Version);
                if (target is null)
                {
                    ProgramLog?.Invoke($"Pending update for '{id}' has no installed target; retaining {entry.SelectedVersion}.");
                    data.Plugins[id] = entry with { PendingUpdate = null };
                    continue;
                }

                try
                {
                    PluginPackageInstaller.ValidateExtractedPackage(target.Path, _isOfficialNamespace, out PackageValidationResult validation);
                    if (!validation.Accepted || !string.Equals(validation.Digest, pending.Digest, StringComparison.OrdinalIgnoreCase))
                    {
                        ProgramLog?.Invoke($"Pending update for '{id}' failed startup validation; retaining {entry.SelectedVersion}.");
                        data.Plugins[id] = entry with { PendingUpdate = null };
                        continue;
                    }

                    data.Plugins[id] = entry with { SelectedVersion = pending.Version, PendingUpdate = null };
                }
                catch (Exception exception)
                {
                    ProgramLog?.Invoke($"Pending update for '{id}' failed startup validation: {exception.Message}");
                    data.Plugins[id] = entry with { PendingUpdate = null };
                }
            }
            return data;
        });
    }

    private void RecoverStartupAttempts()
    {
        foreach ((string id, PluginRegistryEntry entry) in _registry.Current.Plugins.ToList())
        {
            if (entry.LastAttempt is { EndedCleanly: null or false } attempt
                && !string.Equals(attempt.HostRunId, _registry.HostRunId, StringComparison.Ordinal))
            {
                _registry.Mutate(data =>
                {
                    if (data.Plugins.TryGetValue(id, out PluginRegistryEntry? current) && current.LastAttempt == attempt)
                    {
                        FaultDisableRecord fault = current.FaultDisabled?.ActivationId == attempt.ActivationId
                            ? current.FaultDisabled
                            : new FaultDisableRecord
                            {
                                Version = attempt.Version,
                                Digest = attempt.Digest,
                                ActivationId = attempt.ActivationId,
                                Reason = "Previous run did not confirm a clean stop; explicit retry required.",
                                FaultAtUtc = DateTime.UtcNow,
                                ExitConfirmed = false,
                            };
                        if (!attempt.RecoveryNotified && !fault.Notified
                            && !data.PendingNotices.Any(notice => notice.PluginId == id && notice.ActivationId == attempt.ActivationId))
                        {
                            data.PendingNotices.Add(new PendingNoticeRecord
                            {
                                PluginId = id,
                                Version = attempt.Version,
                                Reason = fault.Reason,
                                ActivationId = attempt.ActivationId,
                                ExitConfirmed = fault.ExitConfirmed,
                                Kind = NoticeKinds.Recovery,
                            });
                        }
                        data.Plugins[id] = current with
                        {
                            FaultDisabled = fault,
                            LastAttempt = attempt with { EndedCleanly = false, EndedUtc = attempt.EndedUtc ?? DateTime.UtcNow },
                        };
                    }

                    return data;
                });
            }
        }
    }

    private void MaterializeFactoryPack(FactoryPackDefinition pack)
    {
        ArgumentNullException.ThrowIfNull(pack);
        string targetDir = _installer.GetVersionDirectory(pack.PluginId, pack.Version);
        string staging = Path.Combine(_paths.InstalledRoot, "Staging", $"factory-{pack.PluginId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            foreach (FactoryPackFile file in pack.Files)
            {
                string destination = Path.GetFullPath(Path.Combine(staging, file.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
                if (!destination.StartsWith(Path.GetFullPath(staging) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"Factory pack file '{file.RelativePath}' escapes the staging root.");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.WriteAllBytes(destination, file.Content());
            }

            PluginPackageInstaller.ValidateExtractedPackage(staging, _isOfficialNamespace, out PackageValidationResult result);
            if (!result.Accepted
                || !string.Equals(result.Manifest.Id, pack.PluginId, StringComparison.Ordinal)
                || !string.Equals(result.Manifest.Version, pack.Version, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Factory pack '{pack.PluginId}' {pack.Version} failed validation or identity check: {string.Join("; ", result.Errors)}");
            }

            // Only the current embedded bytes are a trust anchor, never the installed directory
            // or its legacy digest. Same-version rebuilds must go through this comparison too.
            try
            {
                PluginPackageInstaller.ValidateExtractedPackage(targetDir, _isOfficialNamespace, out PackageValidationResult existing);
                if (existing.Accepted
                    && string.Equals(existing.Digest, result.Digest, StringComparison.OrdinalIgnoreCase)
                    && _registry.Current.Plugins.TryGetValue(pack.PluginId, out PluginRegistryEntry? registered)
                    && registered.InstalledVersions.Any(v => v.Version == pack.Version
                        && string.Equals(v.Path, targetDir, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(v.Digest, result.Digest, StringComparison.OrdinalIgnoreCase)))
                {
                    return;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            string? backup = null;
            if (Directory.Exists(targetDir))
            {
                backup = Path.Combine(_paths.InstalledRoot, "Backups", $"factory-{pack.PluginId}-{pack.Version}-{Guid.NewGuid():N}");
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                Directory.Move(targetDir, backup);
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(targetDir)!);
                Directory.Move(staging, targetDir);
            }
            catch
            {
                if (backup is not null) Directory.Move(backup, targetDir);
                throw;
            }
            string digest = result.Digest;
            _registry.Mutate(data =>
            {
                PluginRegistryEntry entry = data.Plugins.TryGetValue(pack.PluginId, out PluginRegistryEntry? existing) ? existing : new PluginRegistryEntry { Id = pack.PluginId };
                List<InstalledVersionRecord> versions = [.. entry.InstalledVersions.Where(v => v.Version != pack.Version)];
                versions.Add(new InstalledVersionRecord
                {
                    Version = pack.Version,
                    Path = targetDir,
                    Digest = digest,
                    InstalledAtUtc = DateTime.UtcNow,
                    Source = InstalledVersionRecord.SourceBuiltIn,
                });
                data.Plugins[pack.PluginId] = entry with
                {
                    InstalledVersions = versions,
                    SelectedVersion = entry.SelectedVersion.Length == 0 ? pack.Version : entry.SelectedVersion,
                    BuiltIn = true,
                    // An empty grant list is an explicit valid decision. Only a newly created
                    // factory entry receives the version's factory-approved initial grants.
                    ApprovedPermissions = entry.InstalledVersions.Count == 0 ? [.. result.Manifest.Permissions] : entry.ApprovedPermissions,
                };
            });
            ProgramLog?.Invoke($"Factory pack '{pack.PluginId}' {pack.Version} materialized from embedded content; digest={digest}; backup={backup ?? "none"}.");
        }
        finally
        {
            try
            {
                if (Directory.Exists(staging))
                {
                    Directory.Delete(staging, recursive: true);
                }
            }
            catch (Exception)
            {
            }
        }
    }

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

    private async Task HandleFaultNoticeAsync(HostFaultNotice notice)
    {
        // Persist first so a UI failure, unavailable dispatcher, or host interruption leaves a
        // recoverable notice rather than a permanently suppressed fault warning.
        _registry.Mutate(data =>
        {
            if (!data.PendingNotices.Any(pending => pending.PluginId == notice.PluginId && pending.ActivationId == notice.ActivationId))
            {
                data.PendingNotices.Add(new PendingNoticeRecord
                {
                    PluginId = notice.PluginId,
                    Version = notice.Version,
                    Reason = notice.Reason,
                    ActivationId = notice.ActivationId,
                    ExitConfirmed = notice.ExitConfirmed,
                    Kind = notice.BudgetProtective ? "budget" : NoticeKinds.Fault,
                });
            }
            return data;
        });
        bool shown;
        try
        {
            shown = await _environment.ShowFaultNoticeAsync(notice).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            ProgramLog?.Invoke($"Fault notice for '{notice.PluginId}' could not be displayed: {exception.Message}");
            shown = false;
        }
        if (shown)
        {
            _registry.Mutate(data =>
            {
                data.PendingNotices.RemoveAll(pending => pending.PluginId == notice.PluginId && pending.ActivationId == notice.ActivationId);
                if (data.Plugins.TryGetValue(notice.PluginId, out PluginRegistryEntry? entry)
                    && entry.FaultDisabled?.ActivationId == notice.ActivationId)
                {
                    data.Plugins[notice.PluginId] = entry with
                    {
                        FaultDisabled = entry.FaultDisabled with { Notified = true },
                        LastAttempt = entry.LastAttempt?.ActivationId == notice.ActivationId
                            ? entry.LastAttempt with { RecoveryNotified = true } : entry.LastAttempt,
                    };
                }
                return data;
            });
        }

        FaultNotice?.Invoke(notice);
        Changed?.Invoke();
    }

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

    private async Task ConsumePendingNoticesAsync()
    {
        PluginRegistryData snapshot = _registry.Current;
        if (snapshot.PendingNotices.Count == 0)
        {
            return;
        }

        foreach (PendingNoticeRecord notice in snapshot.PendingNotices.Where(notice => !notice.Consumed)
            .DistinctBy(notice => (notice.PluginId, notice.ActivationId)).ToList())
        {
            bool shown;
            try
            {
                shown = await _environment.ShowFaultNoticeAsync(new HostFaultNotice
                {
                    PluginId = notice.PluginId,
                    PluginName = notice.PluginId,
                    Version = notice.Version,
                    Reason = notice.Reason,
                    ActivationId = notice.ActivationId,
                    ExitConfirmed = notice.ExitConfirmed,
                    BudgetProtective = string.Equals(notice.Kind, "budget", StringComparison.Ordinal),
                }).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                ProgramLog?.Invoke($"Pending fault notice for '{notice.PluginId}' could not be displayed: {exception.Message}");
                shown = false;
            }
            if (shown)
            {
                _registry.Mutate(data =>
                {
                    data.PendingNotices.RemoveAll(pending => pending.PluginId == notice.PluginId && pending.ActivationId == notice.ActivationId);
                    if (data.Plugins.TryGetValue(notice.PluginId, out PluginRegistryEntry? entry))
                    {
                        data.Plugins[notice.PluginId] = entry with
                        {
                            LastAttempt = entry.LastAttempt?.ActivationId == notice.ActivationId
                                ? entry.LastAttempt with { RecoveryNotified = true } : entry.LastAttempt,
                            FaultDisabled = entry.FaultDisabled?.ActivationId == notice.ActivationId
                                ? entry.FaultDisabled with { Notified = true } : entry.FaultDisabled,
                        };
                    }
                });
            }
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

    public async ValueTask DisposeAsync()
    {
        await StopAllAsync().ConfigureAwait(false);
        _budget.Dispose();
        _launcher.Dispose();
        _hostTempLedger.Dispose();
    }
}
