using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using DuCom.PluginHost;
using DuCom.PluginHost.Core;
using Xunit;
using Xunit.Abstractions;

namespace DuCom.Plugins.Tests;

[Collection("WorkerLifecycle")]
public sealed class LogPackageStreamingTests
{
    private readonly ITestOutputHelper _output;

    public LogPackageStreamingTests(ITestOutputHelper output) => _output = output;

    [HostBuildFact]
    public async Task LargeLogStreamsThroughRealWorkerAndPreservesZipContentHash()
    {
        string root = Path.Combine(Path.GetTempPath(), $"ducom-streaming-zip-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string source = Path.Combine(root, "large.log");
        byte[] block = new byte[64 * 1024];
        for (int index = 0; index < block.Length; index++) block[index] = (byte)(index * 31);
        await using (FileStream stream = File.Create(source))
            for (int index = 0; index < 384; index++) await stream.WriteAsync(block);
        byte[] sourceHash = await SHA256.HashDataAsync(File.OpenRead(source));

        StreamingEnvironment environment = new(source, root);
        await using BedHarness harness = BedHarness.Create("streaming-zip", environment: environment);
        PluginRuntimeController controller = InstallLogPackage(harness);
        Assert.True(await harness.Service.StartRegisteredAsync(controller.Manifest.Id));
        controller = Assert.IsType<PluginRuntimeController>(harness.Service.GetOrCreateController(controller.Manifest.Id));

        using Process worker = Process.GetProcessById(checked((int)controller.WorkerPid));
        using Process host = Process.GetCurrentProcess();
        long hostBaseline = host.PrivateMemorySize64;
        long workerBaseline = worker.PrivateMemorySize64;
        Assert.True(await controller.InvokeCommandAsync("pack", new Dictionary<string, string> { ["projectName"] = "Streaming" }));
        long hostPeak = hostBaseline;
        long workerPeak = workerBaseline;
        DateTime deadline = DateTime.UtcNow.AddSeconds(90);
        string? destination = null;
        while (destination is null && DateTime.UtcNow < deadline)
        {
            host.Refresh();
            worker.Refresh();
            hostPeak = Math.Max(hostPeak, host.PrivateMemorySize64);
            workerPeak = Math.Max(workerPeak, worker.PrivateMemorySize64);
            destination = Directory.GetFiles(root, "*.zip").FirstOrDefault();
            if (destination is null) await Task.Delay(100);
        }
        Assert.NotNull(destination);
        string zipPath = destination;

        using ZipArchive archive = ZipFile.OpenRead(zipPath);
        ZipArchiveEntry logEntry = Assert.Single(archive.Entries, entry => entry.FullName.StartsWith("日志/", StringComparison.Ordinal));
        Assert.Equal(new FileInfo(source).Length, logEntry.Length);
        await using Stream entryStream = logEntry.Open();
        Assert.Equal(sourceHash, await SHA256.HashDataAsync(entryStream));
        long hostGrowth = hostPeak - hostBaseline;
        long workerGrowth = workerPeak - workerBaseline;
        _output.WriteLine($"input={new FileInfo(source).Length:N0} host={hostBaseline:N0}->{hostPeak:N0} growth={hostGrowth:N0} worker={workerBaseline:N0}->{workerPeak:N0} growth={workerGrowth:N0}");
        Assert.True(hostGrowth < 320L * 1024 * 1024, $"Host private commit grew by {hostGrowth:N0} bytes.");
        Assert.True(workerGrowth < 320L * 1024 * 1024, $"Worker private commit grew by {workerGrowth:N0} bytes.");
        File.WriteAllText(Path.Combine(root, "private-commit.txt"), $"hostBaseline={hostBaseline}\nhostPeak={hostPeak}\nhostGrowth={hostGrowth}\nworkerBaseline={workerBaseline}\nworkerPeak={workerPeak}\nworkerGrowth={workerGrowth}\ninputBytes={new FileInfo(source).Length}\n");

        try { Directory.Delete(root, recursive: true); } catch { }
    }

    private static PluginRuntimeController InstallLogPackage(BedHarness harness)
    {
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        string sourceDirectory = Path.Combine(FindWorkspaceRoot(), "src", "DuCom.Plugins.LogPackage", "bin", configuration, "net10.0");
        string target = Path.Combine(harness.Paths.InstalledRoot, "com.ducom.log-package", "1.0.0");
        Directory.CreateDirectory(target);
        foreach (string file in Directory.EnumerateFiles(sourceDirectory))
            if (Path.GetExtension(file) is ".dll" or ".json") File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true);
        File.Copy(Path.Combine(FindWorkspaceRoot(), "src", "DuCom.Plugins.LogPackage", "plugin.manifest.json"), Path.Combine(target, "plugin.manifest.json"), true);
        string digest = DuCom.PluginHost.Packages.PluginPackageInstaller.ComputeDirectoryDigest(target);
        harness.Service.Registry.Mutate(data => data.Plugins["com.ducom.log-package"] = new DuCom.PluginHost.Registry.PluginRegistryEntry
        {
            Id = "com.ducom.log-package", SelectedVersion = "1.0.0", Enabled = true,
            ApprovedPermissions = ["serial.logs.read", "storage.own", "files.user-selected.write"],
            InstalledVersions = [new DuCom.PluginHost.Registry.InstalledVersionRecord { Version = "1.0.0", Path = target, Digest = digest }],
        });
        return Assert.IsType<PluginRuntimeController>(harness.Service.GetOrCreateController("com.ducom.log-package"));
    }

    private static string FindWorkspaceRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "src", "DuCom.Plugins.LogPackage"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("DuCom workspace root not found.");
    }

    private sealed class StreamingEnvironment(string source, string logDirectory) : FakeEnvironment
    {
        public override Task<IReadOnlyList<HostLogSnapshot>> CreateLogSnapshotsAsync(string? sessionId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<HostLogSnapshot>>([new HostLogSnapshot("session-1", "TEST1", [new HostLogSnapshotFile(source, new FileInfo(source).Length, "TEST1", "large.log")])]);

        public override string GetLogDirectory() => logDirectory;
    }

    [HostBuildFact]
    public async Task MultiSelectionExcludesUnselectedLogsAndPreservesDeviceNames()
    {
        await VerifySelectionAsync(new Dictionary<string, string>
        {
            ["selection:s1"] = "true", ["selection:s2"] = "false", ["selection:s3"] = "true",
            ["device:COM1"] = "DeviceOne", ["device:COM3"] = "DeviceThree",
        }, ["COM1", "COM3"]);
    }

    [HostBuildFact]
    public async Task MissingSelectionDefaultsToAllSessions() => await VerifySelectionAsync(new Dictionary<string, string> { ["projectName"] = "Legacy" }, ["COM1", "COM2", "COM3"]);

    [HostBuildFact]
    public async Task EmptySelectionDoesNotCreateArchive() => await VerifySelectionAsync(new Dictionary<string, string>
    {
        ["selection:s1"] = "false", ["selection:s2"] = "false", ["selection:s3"] = "false",
    }, []);

    private static async Task VerifySelectionAsync(Dictionary<string, string> values, string[] expectedPorts)
    {
        string root = Path.Combine(Path.GetTempPath(), $"ducom-selection-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            SelectionEnvironment environment = new(root);
            foreach (string port in new[] { "COM1", "COM2", "COM3" }) await File.WriteAllTextAsync(Path.Combine(root, port + ".log"), port);
            await using BedHarness harness = BedHarness.Create("selection", environment: environment);
            PluginRuntimeController controller = InstallLogPackage(harness);
            Assert.True(await harness.Service.StartRegisteredAsync(controller.Manifest.Id));
            controller = harness.Service.GetOrCreateController(controller.Manifest.Id)!;
            Assert.True(await controller.InvokeCommandAsync("pack", values));
            if (expectedPorts.Length == 0)
            {
                await Task.Delay(1500);
                Assert.Empty(Directory.GetFiles(root, "*.zip"));
                Assert.Empty(environment.RequestedSessions);
                return;
            }
            DateTime deadline = DateTime.UtcNow.AddSeconds(30);
            string? destination = null;
            while (destination is null && DateTime.UtcNow < deadline)
            {
                destination = Directory.GetFiles(root, "*.zip").FirstOrDefault();
                if (destination is null) await Task.Delay(100);
            }
            Assert.NotNull(destination);
            using ZipArchive archive = ZipFile.OpenRead(destination);
            var logs = archive.Entries.Where(entry => entry.FullName.EndsWith(".log", StringComparison.Ordinal)).ToArray();
            Assert.Equal(expectedPorts.Length, logs.Length);
            List<string> contents = [];
            foreach (ZipArchiveEntry entry in logs)
            {
                using StreamReader reader = new(entry.Open());
                contents.Add(await reader.ReadToEndAsync());
            }
            Assert.Equal(expectedPorts, contents.OrderBy(port => port).ToArray());
            if (values.ContainsKey("selection:s1"))
            {
                Assert.Equal(new[] { "s1", "s3" }, environment.RequestedSessions.ToArray());
                Assert.Contains(logs, entry => entry.Name.Contains("DeviceOne"));
                Assert.Contains(logs, entry => entry.Name.Contains("DeviceThree"));
                using StreamReader description = new(archive.Entries.Single(entry => !entry.FullName.EndsWith(".log", StringComparison.Ordinal)).Open());
                Assert.DoesNotContain("COM2", await description.ReadToEndAsync());
            }
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class SelectionEnvironment(string root) : FakeEnvironment
    {
        public System.Collections.Concurrent.ConcurrentQueue<string?> RequestedSessions { get; } = new();
        public override IReadOnlyList<HostSerialSession> GetSerialSessions() => [new("s1", "COM1", true), new("s2", "COM2", true), new("s3", "COM3", true)];
        public override Task<IReadOnlyList<HostLogSnapshot>> CreateLogSnapshotsAsync(string? sessionId, CancellationToken cancellationToken)
        {
            RequestedSessions.Enqueue(sessionId);
            // Deliberately over-return files to exercise the worker's defensive port filter.
            return Task.FromResult<IReadOnlyList<HostLogSnapshot>>(GetSerialSessions().Select(session => new HostLogSnapshot(session.SessionId, session.Port,
                [new HostLogSnapshotFile(Path.Combine(root, session.Port + ".log"), 4, session.Port, session.Port + ".log")])).ToArray());
        }
        public override string GetLogDirectory() => root;
    }
}
