using DuCom.Core.Diagnostics;

namespace DuCom.Core.Tests.Diagnostics;

public sealed class DiagnosticFileLogTests
{
    [Fact]
    public void WritesTimestampLevelMessageAndException()
    {
        using TemporaryDirectory directory = new();
        using DiagnosticFileLog log = new(directory.Path, "ducom.log", maximumFileBytes: 1_024, retainedFileCount: 2);

        log.Information("Application starting.");
        log.Error("Startup failed.", new InvalidOperationException("test failure"));

        string text = ReadShared(Path.Combine(directory.Path, "ducom.log"));
        Assert.Contains("[INFO] [T", text);
        Assert.Contains("] Application starting.", text);
        Assert.Contains("[ERROR] [T", text);
        Assert.Contains("] Startup failed.", text);
        Assert.Contains("InvalidOperationException: test failure", text);
    }

    [Fact]
    public void RotatesOversizedFileAndRetainsConfiguredCount()
    {
        using TemporaryDirectory directory = new();
        string activePath = Path.Combine(directory.Path, "ducom.log");
        File.WriteAllText(activePath, new string('x', 128));
        File.WriteAllText($"{activePath}.1", "older");
        File.WriteAllText($"{activePath}.2", "oldest");

        using DiagnosticFileLog log = new(directory.Path, "ducom.log", maximumFileBytes: 64, retainedFileCount: 2);
        log.Information("new session");

        Assert.True(File.Exists(activePath));
        Assert.True(File.Exists($"{activePath}.1"));
        Assert.True(File.Exists($"{activePath}.2"));
        Assert.False(File.Exists($"{activePath}.3"));
        Assert.Equal(new string('x', 128), File.ReadAllText($"{activePath}.1"));
    }

    [Fact]
    public void RotatesWhenActiveFileExceedsLimitDuringCurrentRun()
    {
        using TemporaryDirectory directory = new();
        string activePath = Path.Combine(directory.Path, "ducom.log");
        using DiagnosticFileLog log = new(directory.Path, "ducom.log", maximumFileBytes: 64, retainedFileCount: 2);

        log.Information(new string('x', 128));
        log.Information("new segment");

        Assert.True(File.Exists($"{activePath}.1"));
        Assert.Contains(new string('x', 128), File.ReadAllText($"{activePath}.1"));
        Assert.Contains("new segment", ReadShared(activePath));
    }

    [Fact]
    public void PruneDirectoryRetainsOnlyNewestFilesWithinLimits()
    {
        using TemporaryDirectory directory = new();
        string oldest = Path.Combine(directory.Path, "ducom-20260101-1.log");
        string middle = Path.Combine(directory.Path, "ducom-20260102-2.log");
        string newest = Path.Combine(directory.Path, "ducom-20260103-3.log");
        File.WriteAllText(oldest, "oldest");
        File.WriteAllText(middle, "middle");
        File.WriteAllText(newest, "newest");
        File.SetLastWriteTimeUtc(oldest, DateTime.UtcNow.AddMinutes(-3));
        File.SetLastWriteTimeUtc(middle, DateTime.UtcNow.AddMinutes(-2));
        File.SetLastWriteTimeUtc(newest, DateTime.UtcNow.AddMinutes(-1));

        DiagnosticFileLog.PruneDirectory(directory.Path, retainedFileCount: 2, maximumAge: TimeSpan.FromDays(1));

        Assert.False(File.Exists(oldest));
        Assert.True(File.Exists(middle));
        Assert.True(File.Exists(newest));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DuCom.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    private static string ReadShared(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }
}
