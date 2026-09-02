using System.Text;
using DuCom.Core.Diagnostics;
using DuCom.Core.Logging;

namespace DuCom.Core.Tests.Logging;

public sealed class SessionLogWriterTests
{
    [Fact]
    public async Task CloseDrainsEveryAcceptedRecord()
    {
        using TemporaryDirectory directory = new();
        LoadMetrics metrics = new();
        await using SessionLogWriter writer = new(new SessionLogWriterOptions(directory.Path, "COM3", 1_024), metrics);
        await writer.StartAsync();

        for (int index = 0; index < 20; index++)
        {
            Assert.True(await writer.WriteAsync(new FormattedLogRecord($"line-{index}\r\n")));
        }

        await writer.StopAsync();

        string text = string.Concat(Directory.GetFiles(directory.Path, "*.txt").Order().Select(File.ReadAllText));
        Assert.Contains("line-0", text);
        Assert.Contains("line-19", text);
        Assert.Equal(20, metrics.Snapshot().WrittenLogRecords);
        Assert.Equal(0, metrics.Snapshot().FormattedLogBlocks);
        Assert.Null(writer.Fault);
    }

    [Fact]
    public async Task RotationCreatesCollisionSafeSegmentsWithoutLosingRecords()
    {
        using TemporaryDirectory directory = new();
        LoadMetrics metrics = new();
        await using SessionLogWriter writer = new(new SessionLogWriterOptions(directory.Path, "COM3", 12, FileNameFormat: "{Port}-{Segment}"), metrics);
        await writer.StartAsync();

        await writer.WriteAsync(new FormattedLogRecord("12345678\r\n"));
        await writer.WriteAsync(new FormattedLogRecord("abcdefgh\r\n"));
        await writer.StopAsync();

        string[] files = Directory.GetFiles(directory.Path, "*.txt");
        Assert.Equal(2, files.Length);
        Assert.Equal("12345678\r\nabcdefgh\r\n", string.Concat(files.Order().Select(File.ReadAllText)));
    }

    [Fact]
    public async Task RotationCanBeDisabledAndFileNameFormatIsApplied()
    {
        using TemporaryDirectory directory = new();
        LoadMetrics metrics = new();
        SessionLogWriterOptions options = new(
            directory.Path,
            "COM:3",
            RotationBytes: 4,
            FileNameFormat: "capture-{Port}-{Segment}",
            RotationEnabled: false);
        await using SessionLogWriter writer = new(options, metrics);
        await writer.StartAsync();

        await writer.WriteAsync(new FormattedLogRecord("12345678"));
        await writer.WriteAsync(new FormattedLogRecord("abcdefgh"));
        await writer.StopAsync();

        string[] files = Directory.GetFiles(directory.Path, "*.txt");
        Assert.Single(files);
        Assert.StartsWith("capture-COM_3-0000", Path.GetFileNameWithoutExtension(files[0]), StringComparison.Ordinal);
        Assert.Equal("12345678abcdefgh", File.ReadAllText(files[0]));
    }

    [Fact]
    public async Task DefaultFileNameFormatUsesReadableTimestampWithoutSegmentSuffix()
    {
        using TemporaryDirectory directory = new();
        LoadMetrics metrics = new();
        await using SessionLogWriter writer = new(new SessionLogWriterOptions(directory.Path, "COM31"), metrics);
        await writer.StartAsync();

        Assert.True(await writer.WriteAsync(new FormattedLogRecord("data")));
        await writer.StopAsync();

        string fileName = Path.GetFileName(Assert.Single(Directory.GetFiles(directory.Path, "*.txt")));
        Assert.Matches(@"^COM31-\d{4}-\d{2}-\d{2} \d{2}-\d{2}-\d{2}\.\d{3}\.txt$", fileName);
    }

    [Fact]
    public async Task DateSubdirectoryUsesReadableYearMonthDayFolder()
    {
        using TemporaryDirectory directory = new();
        LoadMetrics metrics = new();
        DateTimeOffset before = DateTimeOffset.Now;
        await using SessionLogWriter writer = new(
            new SessionLogWriterOptions(directory.Path, "COM31", UseDateSubdirectory: true),
            metrics);

        await writer.StartAsync();
        Assert.True(await writer.WriteAsync(new FormattedLogRecord("data")));
        await writer.StopAsync();
        DateTimeOffset after = DateTimeOffset.Now;

        string[] files = Directory.GetFiles(directory.Path, "*.txt", SearchOption.AllDirectories);
        string file = Assert.Single(files);
        string folder = Path.GetFileName(Path.GetDirectoryName(file))!;
        string beforeFolder = $"{before.Year}-{before.Month}M-{before.Day}D";
        string afterFolder = $"{after.Year}-{after.Month}M-{after.Day}D";
        Assert.True(folder == beforeFolder || folder == afterFolder, $"Unexpected date folder: {folder}");
    }

    [Fact]
    public async Task OutputDirectoryMatchesTheDirectoryContainingTheCurrentLogFile()
    {
        using TemporaryDirectory directory = new();
        SessionLogWriterOptions options = new(directory.Path, "COM31", UseDateSubdirectory: true);
        await using SessionLogWriter writer = new(options, new LoadMetrics());
        await writer.StartAsync();
        Assert.True(await writer.WriteAsync(new FormattedLogRecord("data")));
        await writer.StopAsync();

        string logFile = Assert.Single(Directory.GetFiles(directory.Path, "*.txt", SearchOption.AllDirectories));
        Assert.Equal(writer.OutputDirectory, Path.GetDirectoryName(logFile));
        Assert.Equal(logFile, writer.CurrentFilePath);
    }

    [Fact]
    public async Task InvalidDirectoryProducesExplicitFaultAndRejectsWrites()
    {
        using TemporaryDirectory directory = new();
        string fileAsDirectory = Path.Combine(directory.Path, "not-a-directory");
        await File.WriteAllTextAsync(fileAsDirectory, "x");
        LoadMetrics metrics = new();
        await using SessionLogWriter writer = new(new SessionLogWriterOptions(fileAsDirectory, "COM3", 1_024), metrics);

        await writer.StartAsync();
        bool accepted = await writer.WriteAsync(new FormattedLogRecord("data"));
        await writer.StopAsync();

        Assert.False(accepted);
        Assert.NotNull(writer.Fault);
        Assert.True(metrics.Snapshot().Faults > 0);
    }

    [Fact]
    public async Task ShortDelayBatchFlushesInOrderBeforeClose()
    {
        using TemporaryDirectory directory = new();
        LoadMetrics metrics = new();
        await using SessionLogWriter writer = new(new SessionLogWriterOptions(directory.Path, "COM3", 1_024), metrics);
        await writer.StartAsync();

        Assert.True(await writer.WriteAsync(new FormattedLogRecord("first\r\n")));
        Assert.True(await writer.WriteAsync(new FormattedLogRecord("second\r\n")));

        string path = await WaitForSingleLogFileAsync(directory.Path);
        await WaitUntilAsync(() => ReadSharedText(path) == "first\r\nsecond\r\n");

        Assert.Equal(2, metrics.Snapshot().WrittenLogRecords);
        Assert.Null(writer.Fault);
    }

    [Fact]
    public async Task LargeBatchPreservesOrderAndWrittenMetrics()
    {
        using TemporaryDirectory directory = new();
        LoadMetrics metrics = new();
        await using SessionLogWriter writer = new(new SessionLogWriterOptions(directory.Path, "COM3", 1_000_000), metrics);
        await writer.StartAsync();

        string[] records = Enumerable.Range(0, 2_000).Select(index => $"{index:D4}\n").ToArray();
        foreach (string record in records)
        {
            Assert.True(await writer.WriteAsync(new FormattedLogRecord(record)));
        }

        await writer.StopAsync();

        Assert.Equal(string.Concat(records), string.Concat(Directory.GetFiles(directory.Path, "*.txt").Order().Select(File.ReadAllText)));
        Assert.Equal(records.Length, metrics.Snapshot().WrittenLogRecords);
        Assert.Equal(records.Sum(Encoding.UTF8.GetByteCount), metrics.Snapshot().WrittenLogBytes);
    }

    [Fact]
    public async Task MultipleBatchesUsePeriodicFlushInsteadOfFlushingEveryBatch()
    {
        using TemporaryDirectory directory = new();
        LoadMetrics metrics = new();
        RecordingObserver observer = new();
        SessionLogWriterOptions options = new(
            directory.Path,
            "COM3",
            1_000_000,
            FlushInterval: TimeSpan.FromMilliseconds(150));
        await using SessionLogWriter writer = new(options, metrics, observer);
        await writer.StartAsync();

        for (int index = 0; index < 10; index++)
        {
            Assert.True(await writer.WriteAsync(new FormattedLogRecord($"batch-{index}\r\n")));
            await Task.Delay(35);
        }

        await writer.StopAsync();

        Assert.InRange(observer.Count(SessionLogFlushReason.Periodic), 1, 4);
        Assert.Equal(1, observer.Count(SessionLogFlushReason.Close));
        Assert.Equal(0, observer.Count(SessionLogFlushReason.Fault));
        Assert.Equal(10, metrics.Snapshot().WrittenLogRecords);
    }

    [Fact]
    public async Task RotationAndCloseForceFlush()
    {
        using TemporaryDirectory directory = new();
        RecordingObserver observer = new();
        await using SessionLogWriter writer = new(
            new SessionLogWriterOptions(directory.Path, "COM3", RotationBytes: 8, FlushInterval: TimeSpan.FromSeconds(5)),
            new LoadMetrics(),
            observer);
        await writer.StartAsync();

        await writer.WriteAsync(new FormattedLogRecord("12345678"));
        await writer.WriteAsync(new FormattedLogRecord("abcdefgh"));
        await writer.StopAsync();

        Assert.Equal(1, observer.Count(SessionLogFlushReason.Rotation));
        Assert.Equal(1, observer.Count(SessionLogFlushReason.Close));
    }

    private static async Task<string> WaitForSingleLogFileAsync(string directoryPath)
    {
        string? path = null;
        await WaitUntilAsync(() =>
        {
            path = Directory.GetFiles(directoryPath, "*.txt").SingleOrDefault();
            return path is not null;
        });
        return path!;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private static string ReadSharedText(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using StreamReader reader = new(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DuCom.LogTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, true);
    }

    private sealed class RecordingObserver : ISessionLogWriterObserver
    {
        private readonly List<SessionLogFlushReason> _reasons = [];

        public void Flushed(SessionLogFlushReason reason)
        {
            lock (_reasons)
            {
                _reasons.Add(reason);
            }
        }

        public int Count(SessionLogFlushReason reason)
        {
            lock (_reasons)
            {
                return _reasons.Count(item => item == reason);
            }
        }
    }
}
