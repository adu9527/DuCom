using System.Collections.Concurrent;

namespace DuCom.Core.Logging;

public readonly record struct LogFolderQuotaResult(int DeletedFiles, long DeletedBytes, long RemainingBytes);

/// <summary>
/// Keeps one leaf log folder under a byte quota by deleting the oldest files it owns. Only
/// files matching the caller's managed-file predicate are candidates, and the file currently
/// being written is always protected. Locked or vanished files are skipped and retried on the
/// next pass instead of failing the caller.
/// </summary>
public static class LogFolderQuota
{
    /// <summary>Quota applied to each leaf folder (serial, system, one plugin).</summary>
    public const long DefaultQuotaBytes = 30L * 1024 * 1024;

    private static readonly ConcurrentDictionary<string, long> NextCheckTimestamps = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns true at most once per interval per folder, so a burst of plugin writes does
    /// not rescan the same directory on every file.
    /// </summary>
    public static bool TryBeginCheck(string directory, TimeSpan interval)
    {
        long now = Environment.TickCount64;
        long next = now + (long)interval.TotalMilliseconds;
        while (true)
        {
            long current = NextCheckTimestamps.GetOrAdd(directory, 0);
            if (now < current)
            {
                return false;
            }

            if (NextCheckTimestamps.TryUpdate(directory, next, current) || NextCheckTimestamps.TryAdd(directory, next))
            {
                return true;
            }
        }
    }

    public static LogFolderQuotaResult Enforce(
        string directory,
        long quotaBytes,
        Func<string, bool> isManagedFile,
        IEnumerable<string>? protectedPaths = null)
    {
        ArgumentNullException.ThrowIfNull(isManagedFile);
        if (quotaBytes <= 0)
        {
            return default;
        }

        FileInfo[] files;
        try
        {
            DirectoryInfo info = new(directory);
            if (!info.Exists)
            {
                return default;
            }

            files = info.GetFiles();
        }
        catch (Exception)
        {
            return default;
        }

        HashSet<string> protectedSet = new(protectedPaths ?? [], StringComparer.OrdinalIgnoreCase);
        long total = 0;
        List<FileInfo> candidates = [];
        foreach (FileInfo file in files)
        {
            total += file.Length;
            if (!protectedSet.Contains(file.FullName) && isManagedFile(file.Name))
            {
                candidates.Add(file);
            }
        }

        if (total <= quotaBytes)
        {
            return new(0, 0, total);
        }

        int deletedFiles = 0;
        long deletedBytes = 0;
        foreach (FileInfo file in candidates
            .OrderBy(candidate => candidate.LastWriteTimeUtc)
            .ThenBy(candidate => candidate.Name, StringComparer.Ordinal))
        {
            if (total <= quotaBytes)
            {
                break;
            }

            try
            {
                long length = file.Length;
                file.Delete();
                total -= length;
                deletedBytes += length;
                deletedFiles++;
            }
            catch (Exception)
            {
                // Locked by another reader or already removed: leave it for the next pass.
            }
        }

        return new(deletedFiles, deletedBytes, total);
    }

    /// <summary>Serial receive logs are the only .txt files this application writes.</summary>
    public static bool IsSerialLogFile(string fileName) =>
        fileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);

    /// <summary>One diagnostic log per run.</summary>
    public static bool IsSystemLogFile(string fileName) =>
        fileName.EndsWith(".log", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Artifacts written by plugins through the host (task JSON, audio, packages, records).
    /// Anything a user drops into the folder with another extension is left alone.
    /// </summary>
    public static bool IsPluginArtifactFile(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".json" or ".log" or ".txt" or ".csv" or ".mp3" or ".srt" or ".pcm" or ".sbc" or ".wav" or ".zip" or ".bin" => true,
        _ => false,
    };
}
