using DuCom.Core.Logging;

namespace DuCom.Core.Tests.Logging;

public sealed class LogFolderQuotaTests
{
    [Fact]
    public void DeletesOldestManagedFilesUntilUnderQuota()
    {
        using TemporaryDirectory directory = new();
        string oldest = Write(directory.Path, "a.txt", 100, TimeSpan.FromHours(3));
        string middle = Write(directory.Path, "b.txt", 100, TimeSpan.FromHours(2));
        string newest = Write(directory.Path, "c.txt", 100, TimeSpan.FromHours(1));

        LogFolderQuotaResult result = LogFolderQuota.Enforce(
            directory.Path,
            quotaBytes: 150,
            LogFolderQuota.IsSerialLogFile);

        Assert.Equal(2, result.DeletedFiles);
        Assert.Equal(200, result.DeletedBytes);
        Assert.Equal(100, result.RemainingBytes);
        Assert.False(File.Exists(oldest));
        Assert.False(File.Exists(middle));
        Assert.True(File.Exists(newest));
    }

    [Fact]
    public void SkipsUnmanagedAndProtectedFiles()
    {
        using TemporaryDirectory directory = new();
        string managed = Write(directory.Path, "a.txt", 100, TimeSpan.FromHours(4));
        string unmanaged = Write(directory.Path, "b.exe", 100, TimeSpan.FromHours(3));
        string protectedFile = Write(directory.Path, "c.txt", 100, TimeSpan.FromHours(2));
        string managedNewest = Write(directory.Path, "d.txt", 100, TimeSpan.FromHours(1));

        LogFolderQuotaResult result = LogFolderQuota.Enforce(
            directory.Path,
            quotaBytes: 150,
            LogFolderQuota.IsSerialLogFile,
            [protectedFile]);

        Assert.Equal(2, result.DeletedFiles);
        Assert.False(File.Exists(managed));
        Assert.False(File.Exists(managedNewest));
        Assert.True(File.Exists(unmanaged));
        Assert.True(File.Exists(protectedFile));
    }

    [Fact]
    public void LeavesFolderAloneWhenQuotaIsMet()
    {
        using TemporaryDirectory directory = new();
        Write(directory.Path, "a.txt", 100, TimeSpan.FromHours(2));
        Write(directory.Path, "b.txt", 100, TimeSpan.FromHours(1));

        LogFolderQuotaResult result = LogFolderQuota.Enforce(
            directory.Path,
            quotaBytes: 1_000,
            LogFolderQuota.IsSerialLogFile);

        Assert.Equal(0, result.DeletedFiles);
        Assert.Equal(200, result.RemainingBytes);
        Assert.Equal(2, Directory.GetFiles(directory.Path).Length);
    }

    [Fact]
    public void MissingFolderIsNotAnError()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ducom-missing-{Guid.NewGuid():N}");
        LogFolderQuotaResult result = LogFolderQuota.Enforce(path, 100, _ => true);
        Assert.Equal(0, result.DeletedFiles);
        Assert.Equal(0, result.RemainingBytes);
    }

    [Fact]
    public void PluginArtifactsCoverKnownExtensionsOnly()
    {
        Assert.True(LogFolderQuota.IsPluginArtifactFile("bes-task-abc.json"));
        Assert.True(LogFolderQuota.IsPluginArtifactFile("DuAudio_20261106_101500.MP3"));
        Assert.True(LogFolderQuota.IsPluginArtifactFile("game_mode.pcm"));
        Assert.False(LogFolderQuota.IsPluginArtifactFile("notes.exe"));
        Assert.False(LogFolderQuota.IsPluginArtifactFile("no-extension"));
    }

    [Fact]
    public void CheckThrottleAllowsOneScanPerIntervalPerFolder()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ducom-throttle-{Guid.NewGuid():N}");
        Assert.True(LogFolderQuota.TryBeginCheck(path, TimeSpan.FromMinutes(5)));
        Assert.False(LogFolderQuota.TryBeginCheck(path, TimeSpan.FromMinutes(5)));
        Assert.True(LogFolderQuota.TryBeginCheck(path + "-other", TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void LayoutSeparatesCategoriesUnderOneDateFolder()
    {
        DateTimeOffset now = new(2026, 11, 6, 10, 30, 0, TimeSpan.FromHours(8));
        string root = Path.Combine(Path.GetTempPath(), "logs");

        Assert.Equal(Path.Combine(root, "2026-11-06", "serial"), LogFolderLayout.Serial(root, now));
        Assert.Equal(Path.Combine(root, "2026-11-06", "system"), LogFolderLayout.System(root, now));
        Assert.Equal(
            Path.Combine(root, "2026-11-06", "plugins", "internal.ducom.bes-download"),
            LogFolderLayout.Plugin(root, "internal.ducom.bes-download", now));
    }

    [Fact]
    public void PluginIdsCannotEscapeTheLogRoot()
    {
        string sanitized = LogFolderLayout.SafePluginId("../../evil");
        Assert.DoesNotContain(Path.DirectorySeparatorChar, sanitized);
        Assert.DoesNotContain(Path.AltDirectorySeparatorChar, sanitized);
        Assert.Equal("unknown-plugin", LogFolderLayout.SafePluginId("   "));
    }

    private static string Write(string directory, string name, int bytes, TimeSpan age)
    {
        string path = Path.Combine(directory, name);
        File.WriteAllBytes(path, new byte[bytes]);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - age);
        return path;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DuCom.LogQuota", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, true);
    }
}
