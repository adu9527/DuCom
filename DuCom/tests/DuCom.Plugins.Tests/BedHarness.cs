using System.IO.Compression;
using System.Text.Json;
using DuCom.Plugin;
using DuCom.PluginHost;
using DuCom.PluginHost.Core;
using DuCom.PluginHost.Packages;
using DuCom.PluginHost.Registry;
using Xunit;

namespace DuCom.Plugins.Tests;

public sealed class HostBuildFactAttribute : FactAttribute
{
    public HostBuildFactAttribute()
    {
        if (!BedHarness.TryFindHostExecutable(out string _))
        {
            Skip = "Published DuCom.exe missing (run: powershell -File scripts/build-worker-host.ps1).";
        }
    }
}

public sealed class BedHarness : IAsyncDisposable, IDisposable
{
    private readonly PluginHostPaths _paths;
    private readonly FakeEnvironment _environment;
    private readonly PluginSystemService _service;
    private readonly string _testBedBinaryDirectory;

    public PluginSystemService Service => _service;

    public FakeEnvironment Environment => _environment;

    public string PluginsRoot => _paths.Root;

    public PluginHostPaths Paths => _paths;

    private BedHarness(PluginHostPaths paths, FakeEnvironment environment, PluginSystemService service, string testBedBinaryDirectory)
    {
        _paths = paths;
        _environment = environment;
        _service = service;
        _testBedBinaryDirectory = testBedBinaryDirectory;
    }

    public static bool TryFindHostExecutable(out string path)
    {
        string? configured = System.Environment.GetEnvironmentVariable("DUCOM_TEST_HOST");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            path = configured;
            return File.Exists(path);
        }

        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "artifacts", "worker-host", "DuCom.exe");
            if (File.Exists(candidate))
            {
                path = candidate;
                return true;
            }

            candidate = Path.Combine(directory.FullName, "src", "DuCom", "bin", "Release", "net10.0-windows", "win-x64", "publish", "DuCom.exe");
            if (File.Exists(candidate))
            {
                path = candidate;
                return true;
            }

            directory = directory.Parent;
        }

        path = string.Empty;
        return false;
    }

    public static bool TryFindTestBedOutput(out string path)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Debug";
            string candidate = Path.Combine(directory.FullName, "tests", "DuCom.PluginTestBed", "bin", configuration, "net10.0", "DuCom.PluginTestBed.dll");
            if (File.Exists(candidate))
            {
                path = Path.GetDirectoryName(candidate)!;
                return true;
            }

            directory = directory.Parent;
        }

        path = string.Empty;
        return false;
    }

    public static BedHarness Create(string testId, BudgetGovernorConfig? budget = null, FakeEnvironment? environment = null)
    {
        string root = Path.Combine(Path.GetTempPath(), $"ducom-plugins-test-{testId}-{Guid.NewGuid():N}");
        PluginHostPaths paths = new(root);
        environment ??= new FakeEnvironment();
        Assert.True(TryFindHostExecutable(out string hostExe), "host exe");
        PluginSystemService service = new(paths, environment, hostExe, budget ?? new BudgetGovernorConfig { TotalBudgetBytes = 2_000_000_000, WarningThresholdBytes = 1_600_000_000, PerPluginQuotaBytes = 384 * 1024 * 1024 });
        Assert.True(TryFindTestBedOutput(out string testBed), "test bed output");
        BedHarness harness = new(paths, environment, service, testBed);
        service.InitializeAsync([]).GetAwaiter().GetResult();
        return harness;
    }

    /// <summary>Stages a test-bed variant as an installed package (bypassing .dcpack, keeping full validation).</summary>
    public PluginRegistryEntry InstallBed(string variant, string version = "1.0.0")
    {
        string manifestPath = Path.Combine(_testBedBinaryDirectory, "manifests", variant, "plugin.manifest.json");
        Assert.True(File.Exists(manifestPath), $"manifest for '{variant}' missing: {manifestPath}");
        string json = File.ReadAllText(manifestPath);
        Assert.True(PluginManifestValidator.TryParseStrict(json, out PluginManifest? manifest, out string? parseError), parseError);
        string versionDirectory = new PluginPackageInstaller(_paths.InstalledRoot).GetVersionDirectory(manifest!.Id, version);
        Directory.CreateDirectory(versionDirectory);
        foreach (string file in Directory.GetFiles(_testBedBinaryDirectory))
        {
            string extension = Path.GetExtension(file).ToLowerInvariant();
            if (extension is ".dll" or ".json" && !file.Contains($"{Path.DirectorySeparatorChar}manifests{Path.DirectorySeparatorChar}"))
            {
                File.Copy(file, Path.Combine(versionDirectory, Path.GetFileName(file)), overwrite: true);
            }
        }

        File.WriteAllText(Path.Combine(versionDirectory, "plugin.manifest.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        string digest = PluginPackageInstaller.ComputeDirectoryDigest(versionDirectory);
        PluginRegistryEntry? created = null;
        _service.Registry.Mutate(data =>
        {
            PluginRegistryEntry entry = new() { Id = manifest.Id, SelectedVersion = version, ApprovedPermissions = [.. manifest.Permissions] };
            entry.InstalledVersions.Add(new InstalledVersionRecord { Version = version, Path = versionDirectory, Digest = digest, InstalledAtUtc = DateTime.UtcNow });
            data.Plugins[manifest.Id] = entry;
            created = entry;
        });
        return created!;
    }

    public string ReadPluginStorage(string pluginId)
    {
        string path = Path.Combine(_paths.StorageRoot, pluginId, "config.json");
        return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
    }

    public async ValueTask DisposeAsync()
    {
        await _service.DisposeAsync();
        if (System.Environment.GetEnvironmentVariable("DUCOM_TEST_KEEP") == "1")
        {
            return;
        }

        try
        {
            Directory.Delete(_paths.Root, recursive: true);
        }
        catch (Exception)
        {
        }
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}

public class FakeEnvironment : IPluginHostEnvironment
{
    private readonly List<Action<string, ReadOnlyMemory<byte>, DateTimeOffset>> _rawHandlers = [];

    public string HostVersion => typeof(FakeEnvironment).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public string Culture => "en-US";

    public List<PluginPublishedActivation> Publications { get; } = [];

    public List<HostFaultNotice> FaultNotices { get; } = [];

    public List<(string PluginId, string ContributionId)> ToolPageUpdates { get; } = [];

    public List<BackgroundApply> BackgroundApplies { get; } = [];

    public event EventHandler<string>? SessionClosed;
    public Task<HostSerialLeaseResult> AcquireSerialLeaseAsync(HostSerialLeaseRequest request, CancellationToken cancellationToken) => Task.FromResult(new HostSerialLeaseResult("lease-test", request.Port, "leased", false, false, null));
    public Task<HostSerialLeaseResult> ReleaseSerialLeaseAsync(string pluginId, string activationId, string leaseId, CancellationToken cancellationToken) => Task.FromResult(new HostSerialLeaseResult(leaseId, "TEST1", "released", false, false, null));
    public void RevokeSerialLeases(string pluginId, string activationId) { }

    public virtual IReadOnlyList<HostSerialSession> GetSerialSessions() => [new HostSerialSession("session-1", "TEST1", true)];
    public virtual IReadOnlyList<HostSerialPort> GetSerialPorts() => [new HostSerialPort("COM7", "Fake serial", "1234 / 5678", "fake-device")];

    public virtual Task<IReadOnlyList<HostLogSnapshot>> CreateLogSnapshotsAsync(string? sessionId, CancellationToken cancellationToken)
    {
        string logFile = Path.Combine(Path.GetTempPath(), $"ducom-fake-log-{Guid.NewGuid():N}.txt");
        File.WriteAllText(logFile, new string('L', 10_000));
        return Task.FromResult<IReadOnlyList<HostLogSnapshot>>([new HostLogSnapshot("session-1", "TEST1", [new HostLogSnapshotFile(logFile, 10_000, "TEST1", Path.GetFileName(logFile))])]);
    }

    public IDisposable SubscribeRawBlocks(Action<string, ReadOnlyMemory<byte>, DateTimeOffset> handler)
    {
        lock (_rawHandlers)
        {
            _rawHandlers.Add(handler);
        }

        return new Subscription(() =>
        {
            lock (_rawHandlers)
            {
                _rawHandlers.Remove(handler);
            }
        });
    }

    public void PumpRawBlocks(int count, int blockSize = 1024)
    {
        Action<string, ReadOnlyMemory<byte>, DateTimeOffset>[] handlers;
        lock (_rawHandlers)
        {
            handlers = [.. _rawHandlers];
        }

        byte[] buffer = new byte[blockSize];
        for (int index = 0; index < count; index++)
        {
            foreach (Action<string, ReadOnlyMemory<byte>, DateTimeOffset> handler in handlers)
            {
                handler("session-1", buffer, DateTimeOffset.UtcNow);
            }
        }
    }

    public Task<HostPickResult?> PickReadAsync(string pluginId, HostPickRequest request, CancellationToken cancellationToken) =>
        Task.FromResult<HostPickResult?>(null);

    public virtual Task<HostPickResult?> PickWriteAsync(string pluginId, HostPickRequest request, CancellationToken cancellationToken) =>
        Task.FromResult<HostPickResult?>(null);

    public virtual string GetLogDirectory() => Path.Combine(Path.GetTempPath(), "ducom-fake-logs");

    public List<HostPluginNotice> Notices { get; } = [];

    public Task<bool> ShowNoticeAsync(HostPluginNotice notice)
    {
        Notices.Add(notice);
        return Task.FromResult(true);
    }

    public virtual string? TryResolveRememberedReadPath(string pluginId, string requestedPath) => null;

    public virtual void ForgetRememberedReadPath(string pluginId, string path) { }

    public Task<bool> ShowFaultNoticeAsync(HostFaultNotice notice)
    {
        FaultNotices.Add(notice);
        return Task.FromResult(true);
    }

    public void PublishActivation(PluginPublishedActivation activation) => Publications.Add(activation);

    public virtual void UpdateToolPage(string pluginId, string contributionId, IReadOnlyList<UiNode> nodes) => ToolPageUpdates.Add((pluginId, contributionId));

    public void RemoveActivation(string pluginId)
    {
    }

    public void ApplyBackground(BackgroundApply apply) => BackgroundApplies.Add(apply);

    private sealed class Subscription(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
