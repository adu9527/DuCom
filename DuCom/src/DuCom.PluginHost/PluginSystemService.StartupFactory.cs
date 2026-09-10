using DuCom.PluginHost.Packages;
using DuCom.PluginHost.Registry;

namespace DuCom.PluginHost;

public sealed partial class PluginSystemService
{
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
                        if (!current.BuiltIn && !attempt.RecoveryNotified && !fault.Notified
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
                            FaultDisabled = current.BuiltIn ? fault with { Notified = true } : fault,
                            LastAttempt = attempt with { EndedCleanly = false, EndedUtc = attempt.EndedUtc ?? DateTime.UtcNow, RecoveryNotified = current.BuiltIn || attempt.RecoveryNotified },
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
}
