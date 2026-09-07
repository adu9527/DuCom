using System.IO;
using System.IO.Compression;

namespace DuCom.Services;

internal static class UserDataBackupService
{
    /// <summary>Single source of truth for user-data files that must survive updates.</summary>
    internal static readonly string[] UserDataFileNames =
    [
        "settings.json",
        "highlight-filter-rules.json",
        "shortcuts.json",
        "send-history.json",
        "command-scripts.json",
        "watchdog-rules.json",
        "monitor-rules.json",
        "com0com-preferences.json",
        "mini-log-preferences.json",
        "float-send-global.json",
        "log-package-preferences.json",
    ];

    private const int RetainedBackups = 3;

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
        using ZipArchive archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (string fileName in UserDataFileNames)
        {
            string source = Path.Combine(UserDataDirectory, fileName);
            if (File.Exists(source))
            {
                archive.CreateEntryFromFile(source, fileName, CompressionLevel.Optimal);
            }
        }

        PruneOldBackups();
        return path;
    }

    private static void PruneOldBackups()
    {
        try
        {
            foreach (string stale in Directory.GetFiles(BackupDirectory, "ducom-backup-*.zip")
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Skip(RetainedBackups)
                .Select(file => file.FullName))
            {
                File.Delete(stale);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Retention is best-effort: a locked old backup never blocks a new one.
        }
    }
}
