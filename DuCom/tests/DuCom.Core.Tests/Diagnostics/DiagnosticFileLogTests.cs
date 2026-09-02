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
