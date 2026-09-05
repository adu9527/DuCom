using System.IO.Compression;
using DuCom.Core.Logging;

namespace DuCom.Core.Tests.Logging;

public sealed class LogPackageServiceTests
{
    [Fact]
    public async Task CreatePackageUsesSnapshotLengthAndAvoidsOverwrite()
    {
        using TemporaryDirectory directory = new();
        string logPath = Path.Combine(directory.Path, "COM31-2026-09-05 10-47-06.667.txt");
        await File.WriteAllTextAsync(logPath, "snapshot-new-data");
        LogPackageRequest request = new(
            directory.Path,
            "Project",
            "Issue",
            "Tester",
            "v2.3.4",
            "75%",
            "2026-09-05 12:00:00.000",
            "Observed",
            "Steps",
            "Notes",
            new DateTimeOffset(2026, 9, 5, 12, 1, 2, TimeSpan.Zero).AddMilliseconds(3),
            [new LogPackagePort("COM3", "Left", [new SessionLogFileSnapshot(logPath, 8)])]);

        string first = await LogPackageService.CreateAsync(request);
        string second = await LogPackageService.CreateAsync(request);

        Assert.NotEqual(first, second);
        Assert.EndsWith("-01.zip", second, StringComparison.Ordinal);
        using ZipArchive archive = ZipFile.OpenRead(first);
        ZipArchiveEntry log = Assert.Single(archive.Entries, entry => entry.FullName.StartsWith("日志/", StringComparison.Ordinal));
        Assert.Equal("日志/COM31-2026-09-05 10-47-06.667-Left.txt", log.FullName);
        using StreamReader reader = new(log.Open());
        Assert.Equal("snapshot", await reader.ReadToEndAsync());
        ZipArchiveEntry description = Assert.Single(archive.Entries, entry => entry.FullName == "问题详细描述.txt");
        using StreamReader descriptionReader = new(description.Open());
        string descriptionText = await descriptionReader.ReadToEndAsync();
        Assert.Contains("设备软件版本：v2.3.4", descriptionText, StringComparison.Ordinal);
        Assert.Contains("复现概率：75%", descriptionText, StringComparison.Ordinal);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DuCom.PackageTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
            }
        }
    }
}
