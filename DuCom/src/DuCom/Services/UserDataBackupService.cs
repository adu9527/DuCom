using System.IO;
using System.IO.Compression;

namespace DuCom.Services;

internal static class UserDataBackupService
{
    private static readonly string UserDataDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DuCom");

    internal static string BackupDirectory => Path.Combine(UserDataDirectory, "Backups");

    internal static DateTimeOffset? GetLatestBackupTime()
    {
        if (!Directory.Exists(BackupDirectory))
        {
            return null;
        }

        return Directory.GetFiles(BackupDirectory, "ducom-backup-*.zip")
            .Select(path => new FileInfo(path).LastWriteTimeUtc)
            .OrderDescending()
            .Select(value => (DateTimeOffset?)value)
            .FirstOrDefault();
    }

    internal static string CreateBackup()
    {
        Directory.CreateDirectory(BackupDirectory);
        string path = Path.Combine(BackupDirectory, $"ducom-backup-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        string[] files =
        [
            "settings.json",
            "highlight-filter-rules.json",
            "shortcuts.json",
            "send-history.json",
            "command-scripts.json",
            "watchdog-rules.json",
            "monitor-rules.json",
        ];

        using ZipArchive archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (string fileName in files)
        {
            string source = Path.Combine(UserDataDirectory, fileName);
            if (File.Exists(source))
            {
                archive.CreateEntryFromFile(source, fileName, CompressionLevel.Optimal);
            }
        }

        return path;
    }
}
