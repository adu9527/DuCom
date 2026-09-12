using System.IO.Compression;
using DuCom.PluginHost;
using DuCom.PluginHost.Core;
using DuCom.PluginHost.Packages;
using DuCom.PluginHost.Registry;
using Xunit;

namespace DuCom.Plugins.Tests;

public sealed class PluginSystemServiceRegressionTests : IAsyncLifetime
{
    private const string Id = "org.example.regression";
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ducom-service-regression-{Guid.NewGuid():N}");
    private readonly List<PluginSystemService> _services = [];

    private PluginSystemService Create(IPluginHostEnvironment? environment = null)
    {
        Directory.CreateDirectory(_root);
        string host = Path.Combine(_root, "fixture-host.exe");
        File.WriteAllText(host, "Not executable: these tests must never launch a worker.");
        PluginSystemService service = new(new PluginHostPaths(_root), environment ?? new FakeEnvironment(),
            host, new BudgetGovernorConfig
            {
                TotalBudgetBytes = 8_000_000_000,
                WarningThresholdBytes = 7_000_000_000,
            });
        _services.Add(service);
        return service;
    }

    private string Pack(string version, string[]? permissions = null)
    {
        string stage = Path.Combine(_root, "stage-" + version);
        Directory.CreateDirectory(stage);
        File.WriteAllText(Path.Combine(stage, "plugin.manifest.json"), $$"""
            {
              "manifestVersion": 1, "id": "{{Id}}", "name": "Regression",
              "version": "{{version}}", "protocolVersion": "1.0", "minHostVersion": "0.0.1",
              "entryAssembly": "Test.dll", "entryType": "Test.Plugin",
              "runtime": { "framework": "net10.0", "rid": "win-x64" },
              "capabilities": ["menu"], "permissions": {{System.Text.Json.JsonSerializer.Serialize(permissions ?? [])}}
            }
            """);
        File.WriteAllText(Path.Combine(stage, "Test.dll"), "test fixture");
        string path = Path.Combine(_root, version + ".dcpack");
        ZipFile.CreateFromDirectory(stage, path);
        return path;
    }

    [Fact]
    public async Task FirstInstallSelectsVersionButDoesNotEnableAndUpdateKeepsSelection()
    {
        PluginSystemService service = Create();
        await service.InitializeAsync([]);
        Assert.True(service.InstallPack(Pack("1.0.0")).Accepted);
        Assert.Equal("1.0.0", service.Registry.Current.Plugins[Id].SelectedVersion);
        Assert.False(service.Registry.Current.Plugins[Id].Enabled);
        Assert.NotNull(service.GetOrCreateController(Id));
        Assert.True(service.StageUpdate(Pack("2.0.0")));
        Assert.Equal("1.0.0", service.Registry.Current.Plugins[Id].SelectedVersion);
        Assert.Equal("2.0.0", service.Registry.Current.Plugins[Id].PendingUpdate!.Version);
    }

    [Fact]
    public async Task ProductionCompatibleHostVersionAcceptsBuiltInMinimumAndRetainsOlderHostRejection()
    {
        PluginSystemService compatible = Create(new VersionEnvironment("0.0.1"));
        await compatible.InitializeAsync([]);
        Assert.True(compatible.InstallPack(Pack("1.0.0")).Accepted);
        Assert.NotNull(compatible.GetOrCreateController(Id));

        PluginSystemService older = Create(new VersionEnvironment("0.0.0"));
        await older.InitializeAsync([]);
        Assert.True(older.InstallPack(Pack("1.0.1")).Accepted);
        Assert.Null(older.GetOrCreateController(Id));
        Assert.Contains("host 0.0.0 is below minHostVersion 0.0.1", Assert.Single(older.BuildManagerRows()).FaultReason);
    }

    [Fact]
    public async Task ExistingPluginInstallIsStagedAndNewPermissionsNeedExplicitConsent()
    {
        PluginSystemService service = Create();
        await service.InitializeAsync([]);
        Assert.True(service.InstallPack(Pack("1.0.0")).Accepted);
        Assert.True(service.StageUpdate(Pack("2.0.0", ["storage.own"])));

        PendingUpdateRecord pending = Assert.IsType<PendingUpdateRecord>(service.Registry.Current.Plugins[Id].PendingUpdate);
        Assert.Equal("2.0.0", pending.Version);
        Assert.True(pending.RequiresPermissionConsent);
        Assert.Empty(service.Registry.Current.Plugins[Id].ApprovedPermissions);
    }

    [Fact]
    public async Task NewFactoryBuiltInsAreEnabledByDefaultButExistingUserDisableIsPreserved()
    {
        PluginSystemService service = Create();
        Pack("1.0.0");
        string stage = Path.Combine(_root, "stage-1.0.0");
        FactoryPackDefinition factory = new()
        {
            PluginId = Id,
            Version = "1.0.0",
            Files = Directory.GetFiles(stage).Select(path => new FactoryPackFile(Path.GetFileName(path), () => File.ReadAllBytes(path))).ToList(),
        };

        await service.InitializeAsync([factory]);
        Assert.True(service.Registry.Current.Plugins[Id].Enabled);
        service.SetEnabled(Id, false);

        PluginSystemService restarted = Create();
        await restarted.InitializeAsync([factory]);
        Assert.False(restarted.Registry.Current.Plugins[Id].Enabled);
    }

    [Fact]
    public async Task ControllerOnlyGrantsPermissionsPersistentlyApprovedForTheSelectedVersion()
    {
        PluginSystemService service = Create();
        await service.InitializeAsync([]);
        Assert.True(service.InstallPack(Pack("1.0.0", ["storage.own"])).Accepted);
        service.Registry.Mutate(data =>
        {
            data.Plugins[Id] = data.Plugins[Id] with { ApprovedPermissions = ["storage.own"] };
            return data;
        });

        PluginRuntimeController controller = Assert.IsType<PluginRuntimeController>(service.GetOrCreateController(Id));
        Assert.Equal(["storage.own"], controller.GrantedPermissions);
    }

    [Fact]
    public async Task EmptyApprovalDoesNotGrantPermissionsOnUpdate()
    {
        PluginSystemService service = Create();
        await service.InitializeAsync([]);
        Assert.True(service.InstallPack(Pack("1.0.0")).Accepted);
        service.Registry.Mutate(data =>
        {
            data.Plugins[Id] = data.Plugins[Id] with { ApprovedPermissions = [] };
            return data;
        });

        Assert.True(service.InstallPack(Pack("2.0.0", ["storage.own"])).Accepted);
        Assert.Empty(service.Registry.Current.Plugins[Id].ApprovedPermissions);
        service.Registry.Mutate(data =>
        {
            PluginRegistryEntry entry = data.Plugins[Id];
            data.Plugins[Id] = entry with { SelectedVersion = "2.0.0", Enabled = true };
            return data;
        });

        Assert.Null(service.GetOrCreateController(Id));
        Assert.False(await service.StartRegisteredAsync(Id));
        Assert.Null(service.Registry.Current.Plugins[Id].LastAttempt);
    }

    [Fact]
    public async Task RefusesReplacedInstalledContentBeforeWorkerExecution()
    {
        PluginSystemService service = Create();
        await service.InitializeAsync([]);
        Assert.True(service.InstallPack(Pack("1.0.0")).Accepted);
        PluginRegistryEntry entry = service.Registry.Current.Plugins[Id];
        File.AppendAllText(Path.Combine(entry.InstalledVersions.Single().Path, "Test.dll"), "replacement");
        service.Registry.Mutate(data =>
        {
            data.Plugins[Id] = data.Plugins[Id] with { Enabled = true };
            return data;
        });

        Assert.Null(service.GetOrCreateController(Id));
        Assert.False(await service.StartRegisteredAsync(Id));
        Assert.Null(service.Registry.Current.Plugins[Id].LastAttempt);
    }

    [Theory]
    [InlineData("1.1", "0.0.1", "net10.0", "win-x64")]
    [InlineData("2.0", "0.0.1", "net10.0", "win-x64")]
    [InlineData("1.0", "9.0.0", "net10.0", "win-x64")]
    [InlineData("1.0", "0.0.1", "net9.0", "win-x64")]
    [InlineData("1.0", "0.0.1", "net10.0", "win-arm64")]
    public async Task RefusesIncompatibleRegisteredPackageBeforeWorkerExecution(string protocol, string minHost, string framework, string rid)
    {
        PluginSystemService service = Create();
        await service.InitializeAsync([]);
        Assert.True(service.InstallPack(Pack("1.0.0")).Accepted);
        string manifest = Path.Combine(service.Registry.Current.Plugins[Id].InstalledVersions.Single().Path, "plugin.manifest.json");
        string json = File.ReadAllText(manifest)
            .Replace("\"protocolVersion\": \"1.0\"", $"\"protocolVersion\": \"{protocol}\"")
            .Replace("\"minHostVersion\": \"0.0.1\"", $"\"minHostVersion\": \"{minHost}\"")
            .Replace("\"framework\": \"net10.0\"", $"\"framework\": \"{framework}\"")
            .Replace("\"rid\": \"win-x64\"", $"\"rid\": \"{rid}\"");
        File.WriteAllText(manifest, json);
        string digest = DuCom.PluginHost.Packages.PluginPackageInstaller.ComputeDirectoryDigest(Path.GetDirectoryName(manifest)!);
        service.Registry.Mutate(data =>
        {
            PluginRegistryEntry entry = data.Plugins[Id];
            InstalledVersionRecord version = entry.InstalledVersions.Single();
            entry.InstalledVersions[entry.InstalledVersions.IndexOf(version)] = version with { Digest = digest };
            data.Plugins[Id] = entry with { Enabled = true };
            return data;
        });

        Assert.Null(service.GetOrCreateController(Id));
        Assert.False(await service.StartRegisteredAsync(Id));
        Assert.Null(service.Registry.Current.Plugins[Id].LastAttempt);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task StartRejectsDisabledAndFaultDisabledEvenWithCachedController(bool enabled, bool fault)
    {
        PluginSystemService service = Create();
        await service.InitializeAsync([]);
        Assert.True(service.InstallPack(Pack("1.0.0")).Accepted);
        PluginRuntimeController controller = Assert.IsType<PluginRuntimeController>(service.GetOrCreateController(Id));
        service.Registry.Mutate(data =>
        {
            data.Plugins[Id] = data.Plugins[Id] with
            {
                Enabled = enabled,
                FaultDisabled = fault ? new FaultDisableRecord { Reason = "test" } : null,
            };
        });
        Assert.False(await service.StartRegisteredAsync(Id));
        Assert.Equal(PluginRuntimeState.Discovered, controller.State);
        Assert.Null(service.Registry.Current.Plugins[Id].LastAttempt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UninstallPreservesStorageAndEvictsController(bool asynchronous)
    {
        PluginSystemService service = Create();
        await service.InitializeAsync([]);
        string pack = Pack("1.0.0");
        Assert.True(service.InstallPack(pack).Accepted);
        PluginRuntimeController? first = service.GetOrCreateController(Id);
        Assert.NotNull(first);
        string versionPath = service.Registry.Current.Plugins[Id].InstalledVersions.Single().Path;
        string storage = Path.Combine(service.Paths.StorageRoot, Id, "config.json");
        Directory.CreateDirectory(Path.GetDirectoryName(storage)!);
        File.WriteAllText(storage, "user data");
        if (asynchronous)
            await service.UninstallAsync(Id);
        else
            service.Uninstall(Id);
        Assert.Equal("user data", File.ReadAllText(storage));
        Assert.False(Directory.Exists(versionPath));
        Assert.False(service.Registry.Current.Plugins.ContainsKey(Id));
        Assert.Null(service.GetOrCreateController(Id));
        Assert.True(service.InstallPack(pack).Accepted);
        Assert.NotSame(first, service.GetOrCreateController(Id));
    }

    [Fact]
    public async Task UninstallAllowsSameVersionDifferentContentReplacement()
    {
        PluginSystemService service = Create();
        await service.InitializeAsync([]);
        string first = Pack("1.0.0");
        Assert.True(service.InstallPack(first).Accepted);
        service.Uninstall(Id);

        string stage = Path.Combine(_root, "stage-1.0.0");
        File.WriteAllText(Path.Combine(stage, "Test.dll"), "replacement content");
        File.Delete(first);
        ZipFile.CreateFromDirectory(stage, first);

        PackageValidationResult installed = service.InstallPack(first);
        Assert.True(installed.Accepted);
        Assert.Equal(installed.Digest, service.Registry.Current.Plugins[Id].InstalledVersions.Single().Digest);
    }

    [Fact]
    public async Task ReplaceSameVersionPreservesRegistryStateAndUsesNewContent()
    {
        PluginSystemService service = Create();
        await service.InitializeAsync([]);
        string pack = Pack("1.0.0", ["storage.own"]);
        PackageValidationResult first = service.InstallPack(pack);
        Assert.True(first.Accepted);
        service.SetEnabled(Id, false);

        string stage = Path.Combine(_root, "stage-1.0.0");
        File.WriteAllText(Path.Combine(stage, "Test.dll"), "replacement content");
        File.Delete(pack);
        ZipFile.CreateFromDirectory(stage, pack);
        PackageValidationResult inspected = service.InspectPack(pack);

        PackageValidationResult replaced = await service.ReplaceSameVersionAsync(pack, inspected.Digest);

        PluginRegistryEntry entry = service.Registry.Current.Plugins[Id];
        Assert.True(replaced.Accepted);
        Assert.Equal(inspected.Digest, entry.InstalledVersions.Single().Digest);
        Assert.Contains("storage.own", entry.ApprovedPermissions);
        Assert.False(entry.Enabled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BuiltInPluginCannotBeUninstalled(bool asynchronous)
    {
        PluginSystemService service = Create();
        const string builtInId = "com.ducom.timer";
        string stage = Path.Combine(_root, "built-in-stage");
        Directory.CreateDirectory(stage);
        File.WriteAllText(Path.Combine(stage, "plugin.manifest.json"), "{}");
        FactoryPackDefinition factory = new()
        {
            PluginId = builtInId,
            Version = "1.0.0",
            Files = [new FactoryPackFile("plugin.manifest.json", () => File.ReadAllBytes(Path.Combine(stage, "plugin.manifest.json")))],
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.InitializeAsync([factory]));

        service.Registry.Mutate(data => data.Plugins[builtInId] = new PluginRegistryEntry
        {
            Id = builtInId,
            InstalledVersions = [new InstalledVersionRecord { Version = "1.0.0", Path = stage, Source = InstalledVersionRecord.SourceBuiltIn }],
        });
        if (asynchronous)
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.UninstallAsync(builtInId));
        else
            Assert.Throws<InvalidOperationException>(() => service.Uninstall(builtInId));
        Assert.True(Directory.Exists(stage));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UninstallRefusesUnconfirmedExitWithoutDeletingPackageOrCache(bool fault)
    {
        PluginSystemService service = Create();
        await service.InitializeAsync([]);
        Assert.True(service.InstallPack(Pack("1.0.0")).Accepted);
        PluginRuntimeController? controller = service.GetOrCreateController(Id);
        string versionPath = service.Registry.Current.Plugins[Id].InstalledVersions.Single().Path;
        service.Registry.Mutate(data =>
        {
            data.Plugins[Id] = data.Plugins[Id] with
            {
                LastAttempt = new AttemptRecord { ActivationId = "a1", EndedCleanly = fault ? false : null },
                FaultDisabled = fault ? new FaultDisableRecord { ActivationId = "a1", ExitConfirmed = false } : null,
            };
        });
        Assert.Throws<InvalidOperationException>(() => service.Uninstall(Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.UninstallAsync(Id));
        Assert.True(Directory.Exists(versionPath));
        Assert.True(service.Registry.Current.Plugins.ContainsKey(Id));
        Assert.Same(controller, service.GetOrCreateController(Id));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public async Task RecoveryDisablesBeforeStartAndNotifiesOnlyOnceAcrossRestarts(bool? clean)
    {
        PluginSystemService seed = Create();
        await seed.InitializeAsync([]);
        Assert.True(seed.InstallPack(Pack("1.0.0")).Accepted);
        seed.Registry.Mutate(data =>
        {
            data.Plugins[Id] = data.Plugins[Id] with
            {
                Enabled = true,
                LastAttempt = new AttemptRecord { ActivationId = "old-activation", HostRunId = "old-host", Version = "1.0.0", EndedCleanly = clean },
            };
        });
        FakeEnvironment firstEnvironment = new();
        PluginSystemService first = Create(firstEnvironment);
        await first.InitializeAsync([]);
        Assert.NotNull(first.Registry.Current.Plugins[Id].FaultDisabled);
        Assert.Equal("old-activation", first.Registry.Current.Plugins[Id].LastAttempt!.ActivationId);
        Assert.Single(firstEnvironment.FaultNotices);
        Assert.False(await first.StartRegisteredAsync(Id));
        FakeEnvironment secondEnvironment = new();
        PluginSystemService second = Create(secondEnvironment);
        await second.InitializeAsync([]);
        Assert.Empty(secondEnvironment.FaultNotices);
        Assert.NotNull(second.Registry.Current.Plugins[Id].FaultDisabled);
        Assert.Empty(second.Registry.Current.PendingNotices);
    }

    [Fact]
    public async Task BuiltInRecoveryIsRecordedWithoutPopupAndLegacyNoticeIsRemoved()
    {
        PluginSystemService seed = Create();
        await seed.InitializeAsync([]);
        Assert.True(seed.InstallPack(Pack("1.0.0")).Accepted);
        seed.Registry.Mutate(data =>
        {
            PluginRegistryEntry entry = data.Plugins[Id];
            data.Plugins[Id] = entry with
            {
                BuiltIn = true,
                Enabled = true,
                LastAttempt = new AttemptRecord { ActivationId = "built-in-old", HostRunId = "old-host", Version = "1.0.0" },
            };
            data.PendingNotices.Add(new PendingNoticeRecord { PluginId = Id, Version = "1.0.0", ActivationId = "legacy", Reason = "legacy" });
        });

        FakeEnvironment environment = new();
        PluginSystemService restarted = Create(environment);
        await restarted.InitializeAsync([]);

        Assert.Empty(environment.FaultNotices);
        Assert.DoesNotContain(restarted.Registry.Current.PendingNotices, notice => notice.PluginId == Id);
        Assert.True(restarted.Registry.Current.Plugins[Id].LastAttempt!.RecoveryNotified);
        Assert.NotNull(restarted.Registry.Current.Plugins[Id].FaultDisabled);
    }

    [Fact]
    public async Task UndisplayedRecoveryNoticeIsRetriedWithoutDuplicatingPendingActivation()
    {
        PluginSystemService seed = Create();
        await seed.InitializeAsync([]);
        seed.Registry.Mutate(data =>
        {
            data.Plugins[Id] = new PluginRegistryEntry
            {
                Id = Id,
                LastAttempt = new AttemptRecord { ActivationId = "a1", HostRunId = "old" },
            };
        });
        for (int run = 0; run < 2; run++)
        {
            PluginSystemService deferred = Create(new UnavailableNoticeEnvironment());
            await deferred.InitializeAsync([]);
            Assert.Single(deferred.Registry.Current.PendingNotices);
            Assert.False(deferred.Registry.Current.Plugins[Id].LastAttempt!.RecoveryNotified);
        }
        FakeEnvironment available = new();
        PluginSystemService delivered = Create(available);
        await delivered.InitializeAsync([]);
        Assert.Single(available.FaultNotices);
        Assert.Empty(delivered.Registry.Current.PendingNotices);
        Assert.True(delivered.Registry.Current.Plugins[Id].LastAttempt!.RecoveryNotified);
    }

    [Fact]
    public async Task FaultNoticeDisplayExceptionLeavesThePersistedNoticePending()
    {
        PluginSystemService seed = Create();
        await seed.InitializeAsync([]);
        Assert.True(seed.InstallPack(Pack("1.0.0")).Accepted);
        seed.Registry.Mutate(data =>
        {
            data.Plugins[Id] = data.Plugins[Id] with
            {
                FaultDisabled = new FaultDisableRecord { ActivationId = "a1", Reason = "fault", Notified = false },
                LastAttempt = new AttemptRecord { ActivationId = "a1", EndedCleanly = false },
            };
            return data;
        });
        PluginSystemService service = Create(new ThrowingNoticeEnvironment());
        await service.InitializeAsync([]);
        var controller = Assert.IsType<PluginRuntimeController>(service.GetOrCreateController(Id));

        await controller.FaultAsync("test failure", exitConfirmed: true);

        Assert.Contains(service.Registry.Current.PendingNotices, notice => notice.ActivationId == controller.ActivationId);
        Assert.False(service.Registry.Current.Plugins[Id].FaultDisabled!.Notified);
    }

    [Fact]
    public async Task CorruptRegistryPreventsFactoryAutoStart()
    {
        PluginSystemService service = Create();
        Directory.CreateDirectory(Path.GetDirectoryName(service.Paths.RegistryPath)!);
        File.WriteAllText(service.Paths.RegistryPath, "broken json");
        Pack("1.0.0");
        string stage = Path.Combine(_root, "stage-1.0.0");
        await service.InitializeAsync([new FactoryPackDefinition
        {
            PluginId = Id,
            Version = "1.0.0",
            Files = Directory.GetFiles(stage).Select(path => new FactoryPackFile(Path.GetFileName(path), () => File.ReadAllBytes(path))).ToList(),
        }]);
        Assert.True(service.SafeStartAllPlugins);
        Assert.True(service.Registry.Current.Plugins[Id].Enabled);
        Assert.Null(service.Registry.Current.Plugins[Id].LastAttempt);
        Assert.False(await service.StartRegisteredAsync(Id));
    }

    public Task InitializeAsync() => Task.CompletedTask;

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task FactoryRefreshUsesEmbeddedContentAndPreservesUserState(bool legacyDigest, bool changedContent)
    {
        PluginSystemService seed = Create();
        await seed.InitializeAsync([]);
        Assert.True(seed.InstallPack(Pack("1.0.0", ["storage.own"])).Accepted);
        PluginRegistryEntry original = seed.Registry.Current.Plugins[Id];
        string target = original.InstalledVersions.Single().Path;
        string originalContent = File.ReadAllText(Path.Combine(target, "Test.dll"));
        FaultDisableRecord fault = new() { ActivationId = "fault", Reason = "keep disabled", Notified = true, ExitConfirmed = true };
        AttemptRecord attempt = new() { ActivationId = "fault", HostRunId = "old", EndedCleanly = true };
        RememberedGrantRecord grant = new() { Path = "remembered.txt" };
        seed.Registry.Mutate(data =>
        {
            data.Plugins[Id] = original with
            {
                BuiltIn = true,
                Enabled = true,
                ApprovedPermissions = [],
                RememberedGrants = [grant],
                FaultDisabled = fault,
                LastAttempt = attempt,
                InstalledVersions = [original.InstalledVersions.Single() with
                {
                    Digest = legacyDigest ? new string('A', 64) : original.InstalledVersions.Single().Digest,
                    Source = InstalledVersionRecord.SourceBuiltIn,
                }],
            };
        });
        string config = Path.Combine(seed.Paths.StorageRoot, Id, "config.json");
        Directory.CreateDirectory(Path.GetDirectoryName(config)!);
        File.WriteAllText(config, "user configuration");
        string stage = Path.Combine(_root, "stage-1.0.0");
        if (changedContent) File.WriteAllText(Path.Combine(stage, "Test.dll"), "current embedded build");
        FactoryPackDefinition factory = new()
        {
            PluginId = Id, Version = "1.0.0",
            Files = Directory.GetFiles(stage).Select(path => new FactoryPackFile(Path.GetFileName(path), () => File.ReadAllBytes(path))).ToList(),
        };

        PluginSystemService restarted = Create();
        await restarted.InitializeAsync([factory]);
        PluginRegistryEntry refreshed = restarted.Registry.Current.Plugins[Id];
        Assert.Equal(DuCom.PluginHost.Packages.PluginPackageInstaller.ComputeDirectoryDigest(stage), refreshed.InstalledVersions.Single().Digest);
        Assert.Equal(File.ReadAllText(Path.Combine(stage, "Test.dll")), File.ReadAllText(Path.Combine(target, "Test.dll")));
        string backup = Assert.Single(Directory.GetDirectories(Path.Combine(restarted.Paths.InstalledRoot, "Backups")));
        Assert.Equal(originalContent, File.ReadAllText(Path.Combine(backup, "Test.dll")));
        Assert.Equal("user configuration", File.ReadAllText(config));
        Assert.Empty(refreshed.ApprovedPermissions);
        Assert.Equal(grant, Assert.Single(refreshed.RememberedGrants));
        Assert.Equal(fault, refreshed.FaultDisabled);
        Assert.Equal(attempt, refreshed.LastAttempt);
        Assert.True(refreshed.Enabled);
        Assert.False(await restarted.StartRegisteredAsync(Id));
        Assert.Equal(PluginRuntimeState.FaultDisabled, Assert.Single(restarted.BuildManagerRows()).State);

        PluginSystemService next = Create();
        await next.InitializeAsync([factory]);
        Assert.Single(Directory.GetDirectories(Path.Combine(next.Paths.InstalledRoot, "Backups")));
        next.ClearFaultDisable(Id);
        Assert.Null(next.GetOrCreateController(Id));
        Assert.Contains("not been approved", Assert.Single(next.BuildManagerRows()).FaultReason);
        next.Registry.Mutate(data => { data.Plugins[Id] = data.Plugins[Id] with { ApprovedPermissions = ["storage.own"] }; });
        Assert.NotNull(next.GetOrCreateController(Id));
        Assert.Empty(Assert.Single(next.BuildManagerRows()).FaultReason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommunityDigestMismatchRemainsRejectedAcrossRestart(bool legacyDigest)
    {
        PluginSystemService seed = Create();
        await seed.InitializeAsync([]);
        Assert.True(seed.InstallPack(Pack("1.0.0")).Accepted);
        InstalledVersionRecord installed = seed.Registry.Current.Plugins[Id].InstalledVersions.Single();
        if (!legacyDigest) File.AppendAllText(Path.Combine(installed.Path, "Test.dll"), "tampered");
        seed.Registry.Mutate(data =>
        {
            data.Plugins[Id] = data.Plugins[Id] with
            {
                Enabled = true,
                InstalledVersions = [installed with { Digest = legacyDigest ? new string('B', 64) : installed.Digest }],
            };
        });
        PluginSystemService restarted = Create();
        await restarted.InitializeAsync([]);
        Assert.Null(restarted.Registry.Current.Plugins[Id].LastAttempt);
        PluginManagerRow row = Assert.Single(restarted.BuildManagerRows());
        Assert.Equal(PluginRuntimeState.Rejected, row.State);
        Assert.Contains("registered identity or digest", row.FaultReason);
        Assert.Contains("actual=", row.FaultReason);
        Assert.Equal(legacyDigest ? new string('B', 64) : installed.Digest, restarted.Registry.Current.Plugins[Id].InstalledVersions.Single().Digest);
    }

    [Fact]
    public async Task RevalidationRejectionOverridesNonRunningCachedController()
    {
        PluginSystemService service = Create();
        await service.InitializeAsync([]);
        Assert.True(service.InstallPack(Pack("1.0.0")).Accepted);
        Assert.NotNull(service.GetOrCreateController(Id));
        string dll = Path.Combine(service.Registry.Current.Plugins[Id].InstalledVersions.Single().Path, "Test.dll");
        File.AppendAllText(dll, "tampered");
        int changes = 0;
        service.Changed += () => changes++;
        Assert.Null(service.GetOrCreateController(Id));
        Assert.Equal(PluginRuntimeState.Rejected, Assert.Single(service.BuildManagerRows()).State);
        Assert.True(changes > 0);
    }

    [Fact]
    public async Task FactoryIdentityMismatchDoesNotReplaceInstalledPackageOrRegistry()
    {
        PluginSystemService seed = Create();
        await seed.InitializeAsync([]);
        Assert.True(seed.InstallPack(Pack("1.0.0")).Accepted);
        InstalledVersionRecord installed = seed.Registry.Current.Plugins[Id].InstalledVersions.Single();
        string stage = Path.Combine(_root, "stage-1.0.0");
        string manifest = Path.Combine(stage, "plugin.manifest.json");
        File.WriteAllText(manifest, File.ReadAllText(manifest).Replace(Id, "org.example.wrong"));
        PluginSystemService restarted = Create();
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.InitializeAsync([new FactoryPackDefinition
        {
            PluginId = Id, Version = "1.0.0",
            Files = Directory.GetFiles(stage).Select(path => new FactoryPackFile(Path.GetFileName(path), () => File.ReadAllBytes(path))).ToList(),
        }]));
        Assert.Equal(installed, restarted.Registry.Current.Plugins[Id].InstalledVersions.Single());
        Assert.Equal(installed.Digest, DuCom.PluginHost.Packages.PluginPackageInstaller.ComputeDirectoryDigest(installed.Path));
        Assert.False(Directory.Exists(Path.Combine(restarted.Paths.InstalledRoot, "Backups")));
    }

    [Theory]
    [InlineData(false, "shown")]
    [InlineData(false, "unavailable")]
    [InlineData(false, "exception")]
    [InlineData(false, "shutdown")]
    [InlineData(true, "shown")]
    [InlineData(true, "unavailable")]
    [InlineData(true, "exception")]
    [InlineData(true, "shutdown")]
    public async Task NoticeIsConsumedOnlyAfterAsyncDisplayConfirmation(bool recovery, string outcome)
    {
        DeferredNoticeEnvironment environment = new();
        PluginSystemService service = Create(environment);
        await service.InitializeAsync([]);
        Assert.True(service.InstallPack(Pack("1.0.0")).Accepted);
        Task completed;
        string activationId;
        if (recovery)
        {
            activationId = "old-activation";
            service.Registry.Mutate(data =>
            {
                data.Plugins[Id] = data.Plugins[Id] with
                {
                    LastAttempt = new AttemptRecord { ActivationId = activationId, HostRunId = "old-host" },
                };
            });
            service = Create(environment);
            completed = service.InitializeAsync([]);
        }
        else
        {
            TaskCompletionSource handled = new(TaskCreationOptions.RunContinuationsAsynchronously);
            service.FaultNotice += _ => handled.TrySetResult();
            PluginRuntimeController controller = Assert.IsType<PluginRuntimeController>(service.GetOrCreateController(Id));
            await controller.FaultAsync("test failure", exitConfirmed: true);
            activationId = controller.ActivationId;
            completed = handled.Task;
        }

        await environment.Requested.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(completed.IsCompleted);
        Assert.Single(service.Registry.Current.PendingNotices);
        Assert.False(service.Registry.Current.Plugins[Id].FaultDisabled!.Notified);
        // Check the durable state as well as the in-memory snapshot while UI work is queued.
        PluginRegistryStore persisted = new(service.Paths.RegistryPath);
        persisted.Load();
        Assert.Single(persisted.Current.PendingNotices);

        switch (outcome)
        {
            case "shown": environment.Displayed.SetResult(true); break;
            case "unavailable": environment.Displayed.SetResult(false); break;
            case "exception": environment.Displayed.SetException(new InvalidOperationException("Show failed")); break;
            case "shutdown": environment.Displayed.SetCanceled(); break;
        }
        await completed.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(outcome == "shown", service.Registry.Current.Plugins[Id].FaultDisabled!.Notified);
        if (outcome == "shown")
        {
            Assert.Empty(service.Registry.Current.PendingNotices);
        }
        else
        {
            Assert.Equal(activationId, Assert.Single(service.Registry.Current.PendingNotices).ActivationId);
        }

        FakeEnvironment restartedEnvironment = new();
        PluginSystemService restarted = Create(restartedEnvironment);
        await restarted.InitializeAsync([]);
        Assert.Equal(outcome == "shown" ? 0 : 1, restartedEnvironment.FaultNotices.Count);
        Assert.Empty(restarted.Registry.Current.PendingNotices);
    }

    private sealed class DeferredNoticeEnvironment : UnavailableNoticeEnvironment
    {
        public TaskCompletionSource Requested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Displayed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override Task<bool> ShowFaultNoticeAsync(HostFaultNotice notice)
        {
            Requested.TrySetResult();
            return Displayed.Task;
        }
    }

    private class UnavailableNoticeEnvironment : IPluginHostEnvironment
    {
        public virtual string HostVersion => "1.0.0";
        public string Culture => "en-US";
        public event EventHandler<string>? SessionClosed { add { } remove { } }
        public Task<HostSerialLeaseResult> AcquireSerialLeaseAsync(HostSerialLeaseRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<HostSerialLeaseResult> ReleaseSerialLeaseAsync(string pluginId, string activationId, string leaseId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public void RevokeSerialLeases(string pluginId, string activationId) { }
        public virtual Task<bool> ShowFaultNoticeAsync(HostFaultNotice notice) => Task.FromResult(false);
        public IReadOnlyList<HostSerialSession> GetSerialSessions() => [];
        public IReadOnlyList<HostSerialPort> GetSerialPorts() => [];
        public Task<IReadOnlyList<HostLogSnapshot>> CreateLogSnapshotsAsync(string? sessionId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public IDisposable SubscribeRawBlocks(Action<string, ReadOnlyMemory<byte>, DateTimeOffset> handler) => throw new NotSupportedException();
        public Task<HostPickResult?> PickReadAsync(string pluginId, HostPickRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<HostPickResult?> PickWriteAsync(string pluginId, HostPickRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public string GetLogDirectory() => throw new NotSupportedException();
        public Task<bool> ShowNoticeAsync(HostPluginNotice notice) => Task.FromResult(false);
        public string? TryResolveRememberedReadPath(string pluginId, string requestedPath) => null;
        public void ForgetRememberedReadPath(string pluginId, string path) { }
        public void PublishActivation(PluginPublishedActivation activation) => throw new NotSupportedException();
        public void UpdateToolPage(string pluginId, string contributionId, IReadOnlyList<DuCom.Plugin.UiNode> nodes) => throw new NotSupportedException();
        public void RemoveActivation(string pluginId) { }
        public void ApplyBackground(BackgroundApply apply) => throw new NotSupportedException();
    }

    private sealed class ThrowingNoticeEnvironment : UnavailableNoticeEnvironment
    {
        public override Task<bool> ShowFaultNoticeAsync(HostFaultNotice notice) => throw new InvalidOperationException("UI unavailable");
    }

    private sealed class VersionEnvironment(string hostVersion) : UnavailableNoticeEnvironment
    {
        public override string HostVersion => hostVersion;
    }

    public async Task DisposeAsync()
    {
        foreach (PluginSystemService service in _services)
            await service.DisposeAsync();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
