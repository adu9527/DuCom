using System.Text;

namespace DuCom.Core.Persistence;

public sealed record AtomicFileWrite(string Path, byte[] Content);

/// <summary>
/// All-or-nothing multi-file commit for JSON stores: every write is staged to a temp file
/// in the destination directory first, existing destinations are backed up, and only then
/// are the temp files moved into place. Any failure (any exception type, not only IO)
/// rolls every already-replaced file back from its backup on a best-effort basis; when a
/// rollback step fails, the remaining files are still restored, the backup files are kept
/// on disk for manual recovery, and one <see cref="AggregateException"/> surfaces the
/// original failure together with every rollback failure — a failed commit never leaves a
/// half-migrated store and never hides what went wrong.
/// </summary>
public static class AtomicFileStore
{
    /// <summary>
    /// Test-only seam (InternalsVisibleTo DuCom.Core.Tests): when non-null, this factory
    /// is consulted for every rollback step; a returned exception is injected as that
    /// step's failure so the keep-backup/aggregate behavior is deterministically testable.
    /// </summary>
    internal static Func<string, Exception?>? RollbackFault;

    public static void WriteAllText(string path, string content)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(content);

        string directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException($"Cannot determine the directory of '{path}'.");
        Directory.CreateDirectory(directory);
        string temp = Path.Combine(directory, $"{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temp, EncodeUtf8(content));
            if (File.Exists(path))
            {
                File.Replace(temp, path, null);
            }
            else
            {
                File.Move(temp, path);
            }
        }
        finally
        {
            try
            {
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }
            catch (Exception)
            {
                // Cleanup must not replace the original write/replace exception.
            }
        }
    }

    public static void CommitAll(IEnumerable<AtomicFileWrite> writes)
    {
        ArgumentNullException.ThrowIfNull(writes);
        List<(string Destination, string Temp, string? Backup, bool Replaced)> staged = [];
        List<string> keptBackups = [];
        Exception? failure = null;
        List<Exception> rollbackFailures = [];
        try
        {
            foreach (AtomicFileWrite write in writes)
            {
                string directory = Path.GetDirectoryName(write.Path)
                    ?? throw new InvalidOperationException($"Cannot determine the directory of '{write.Path}'.");
                Directory.CreateDirectory(directory);
                string temp = Path.Combine(directory, $"{Path.GetFileName(write.Path)}.{Guid.NewGuid():N}.tmp");
                File.WriteAllBytes(temp, write.Content);
                staged.Add((write.Path, temp, null, Replaced: false));
            }

            // Backups are created by File.Replace below; a destination that does not exist
            // yet gets a plain move and is rolled back by deleting the new file.
            foreach ((string Destination, string Temp, string? Backup, bool Replaced) entry in staged.ToArray())
            {
                string backup = entry.Destination + ".migrate-bak";
                if (File.Exists(entry.Destination))
                {
                    File.Replace(entry.Temp, entry.Destination, backup);
                    SetBackup(staged, entry.Destination, backup);
                }
                else
                {
                    File.Move(entry.Temp, entry.Destination);
                    MarkReplaced(staged, entry.Destination);
                }
            }
        }
        catch (Exception exception)
        {
            failure = exception;
            Rollback(staged, keptBackups, rollbackFailures);
        }
        finally
        {
            Cleanup(staged, keepBackups: failure is not null || rollbackFailures.Count > 0, keptBackups);
        }

        if (failure is not null || rollbackFailures.Count > 0)
        {
            List<Exception> all = [];
            if (failure is not null)
            {
                all.Add(failure);
            }

            all.AddRange(rollbackFailures);
            if (keptBackups.Count > 0)
            {
                throw new AggregateException(
                    $"Atomic commit failed and {keptBackups.Count} rollback backup file(s) were kept on disk for manual recovery: {string.Join(", ", keptBackups)}",
                    all);
            }

            throw new AggregateException("Atomic commit failed; every replaced file was rolled back.", all);
        }
    }

    public static byte[] EncodeUtf8(string content) => new UTF8Encoding(false).GetBytes(content);

    private static void SetBackup(List<(string Destination, string Temp, string? Backup, bool Replaced)> staged, string destination, string backup)
    {
        for (int index = 0; index < staged.Count; index++)
        {
            if (string.Equals(staged[index].Destination, destination, StringComparison.OrdinalIgnoreCase))
            {
                staged[index] = (staged[index].Destination, staged[index].Temp, backup, Replaced: true);
                return;
            }
        }
    }

    private static void MarkReplaced(List<(string Destination, string Temp, string? Backup, bool Replaced)> staged, string destination)
    {
        for (int index = 0; index < staged.Count; index++)
        {
            if (string.Equals(staged[index].Destination, destination, StringComparison.OrdinalIgnoreCase))
            {
                staged[index] = (staged[index].Destination, staged[index].Temp, null, Replaced: true);
                return;
            }
        }
    }

    /// <summary>
    /// Best-effort rollback for every already-replaced destination, catching every exception
    /// type so one failing file cannot prevent restoring the others. Backups that cannot be
    /// restored are recorded and kept on disk.
    /// </summary>
    private static void Rollback(
        List<(string Destination, string Temp, string? Backup, bool Replaced)> staged,
        List<string> keptBackups,
        List<Exception> rollbackFailures)
    {
        foreach ((string Destination, string Temp, string? Backup, bool Replaced) entry in staged)
        {
            if (!entry.Replaced)
            {
                continue;
            }

            try
            {
                Exception? injected = RollbackFault?.Invoke(entry.Destination);
                if (injected is not null)
                {
                    throw injected;
                }

                if (entry.Backup is not null && File.Exists(entry.Backup))
                {
                    File.Replace(entry.Backup, entry.Destination, null);
                }
                else
                {
                    File.Delete(entry.Destination);
                }
            }
            catch (Exception exception)
            {
                rollbackFailures.Add(new IOException($"Rollback of '{entry.Destination}' failed: {exception.Message}", exception));
                if (entry.Backup is not null && File.Exists(entry.Backup) && !keptBackups.Contains(entry.Backup, StringComparer.OrdinalIgnoreCase))
                {
                    keptBackups.Add(entry.Backup);
                }
            }
        }
    }

    private static void Cleanup(
        List<(string Destination, string Temp, string? Backup, bool Replaced)> staged,
        bool keepBackups,
        List<string> keptBackups)
    {
        foreach ((string Destination, string Temp, string? Backup, bool Replaced) entry in staged)
        {
            TryDelete(entry.Temp);
            if (entry.Backup is not null)
            {
                if (keepBackups)
                {
                    // Keep every backup when the commit failed so manual recovery is
                    // possible even for files whose own rollback succeeded.
                    if (File.Exists(entry.Backup) && !keptBackups.Contains(entry.Backup, StringComparer.OrdinalIgnoreCase))
                    {
                        keptBackups.Add(entry.Backup);
                    }
                }
                else
                {
                    TryDelete(entry.Backup);
                }
            }
        }

        static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception)
            {
                // Best-effort cleanup: leftover temp/backup files never corrupt a store.
            }
        }
    }
}
