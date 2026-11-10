using System.Collections.Concurrent;

namespace DuCom.Core.Logging;

public readonly record struct LogFolderQuotaResult(int DeletedFiles, long DeletedBytes, long RemainingBytes);

/// <summary>
/// Applies a soft byte quota to owned files in one leaf folder, oldest first. Unmanaged files
/// neither count toward the quota nor become deletion candidates. Explicit paths and live
/// writer leases (active and most recent complete segments) are exempt, so retention can
/// exceed the quota. Locked or vanished files are skipped instead of failing the caller.
/// </summary>
public static class LogFolderQuota
{
    /// <summary>Soft quota for owned files; protected active/recent segments may exceed it.</summary>
    public const long DefaultQuotaBytes = 30L * 1024 * 1024;

    private static readonly ConcurrentDictionary<string, long> NextCheckTimestamps = new(StringComparer.OrdinalIgnoreCase);
    internal static readonly object SyncRoot = new();
    private static readonly Dictionary<string, int> ProtectedFiles = new(StringComparer.OrdinalIgnoreCase);
    private const string OwnershipSuffix = ".ducom-owned";
    private const string OwnershipContent = "DuCom session log v1";

    internal static IDisposable Protect(string path)
    {
        path = Path.GetFullPath(path);
        lock (SyncRoot)
        {
            ProtectedFiles.TryGetValue(path, out int count);
            ProtectedFiles[path] = count + 1;
        }

        return new Protection(path);
    }

    private sealed class Protection(string path) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            lock (SyncRoot)
            {
                if (_disposed) return;
                _disposed = true;
                if (ProtectedFiles[path] == 1) ProtectedFiles.Remove(path);
                else ProtectedFiles[path]--;
            }
        }
    }

    internal static bool IsProtected(string path)
    {
        lock (SyncRoot) return ProtectedFiles.ContainsKey(Path.GetFullPath(path));
    }

    internal static void MarkSerialFile(string path)
    {
        // Never adopt historical files by extension or a user-customizable name.
        try
        {
            using StreamWriter marker = new(new FileStream(path + OwnershipSuffix, FileMode.CreateNew, FileAccess.Write));
            marker.Write(OwnershipContent);
        }
        catch (IOException) { /* No ownership proof: retain rather than risk another file. */ }
        catch (UnauthorizedAccessException) { /* Logging itself must still work. */ }
    }

    internal static bool IsOwnedSerialFile(string directory, string fileName)
    {
        if (!IsSerialLogFile(fileName)) return false;
        try { return File.ReadAllText(Path.Combine(directory, fileName) + OwnershipSuffix) == OwnershipContent; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

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
        lock (SyncRoot)
        {
            return EnforceCore(directory, quotaBytes, isManagedFile, protectedPaths);
        }
    }

    private static LogFolderQuotaResult EnforceCore(
        string directory,
        long quotaBytes,
        Func<string, bool> isManagedFile,
        IEnumerable<string>? protectedPaths)
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

        HashSet<string> protectedSet = new((protectedPaths ?? []).Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
        protectedSet.UnionWith(ProtectedFiles.Keys);
        long total = 0;
        List<FileInfo> candidates = [];
        foreach (FileInfo file in files)
        {
            if (!isManagedFile(file.Name)) continue;
            total += file.Length;
            if (!protectedSet.Contains(file.FullName))
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
                string marker = file.FullName + OwnershipSuffix;
                if (File.Exists(marker) && File.ReadAllText(marker) == OwnershipContent) File.Delete(marker);
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

    /// <summary>Host diagnostic logs, including numeric rotation backups, never arbitrary .log files.</summary>
    public static bool IsSystemLogFile(string fileName)
    {
        string name = fileName;
        int logEnd = name.LastIndexOf(".log", StringComparison.OrdinalIgnoreCase);
        if (logEnd < 0) return false;
        string suffix = name[(logEnd + 4)..];
        if (suffix.Length > 0 && (suffix[0] != '.' ||
            !int.TryParse(suffix.AsSpan(1), out int backup) || backup <= 0)) return false;
        string stem = name[..logEnd];
        return stem.Equals("ducom", StringComparison.OrdinalIgnoreCase) ||
            System.Text.RegularExpressions.Regex.IsMatch(stem, @"^ducom-\d{8}-(?:\d{6}-)?\d+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    }

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
