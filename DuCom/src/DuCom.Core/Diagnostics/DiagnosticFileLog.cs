using System.Globalization;
using System.Text;
using DuCom.Core.Logging;

namespace DuCom.Core.Diagnostics;

public sealed class DiagnosticFileLog : IDisposable
{
    private readonly object _syncRoot = new();
    private readonly string _activePath;
    private readonly long _maximumFileBytes;
    private readonly int _retainedFileCount;
    private StreamWriter? _writer;
    private readonly long _folderQuotaBytes;
    private IDisposable? _activeProtection;
    private IDisposable? _completedProtection;

    public DiagnosticFileLog(
        string directoryPath,
        string fileName = "ducom.log",
        long maximumFileBytes = 10 * 1024 * 1024,
        int retainedFileCount = 3,
        long folderQuotaBytes = LogFolderQuota.DefaultQuotaBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumFileBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(retainedFileCount);

        _activePath = Path.Combine(directoryPath, fileName);
        _maximumFileBytes = maximumFileBytes;
        _retainedFileCount = retainedFileCount;
        _folderQuotaBytes = folderQuotaBytes;

        try
        {
            lock (LogFolderQuota.SyncRoot)
            {
                Directory.CreateDirectory(directoryPath);
                _activeProtection = LogFolderQuota.Protect(_activePath);
                RotateIfRequired();
                _writer = OpenWriter();
                if (_folderQuotaBytes > 0 && _completedProtection is null && _retainedFileCount > 0)
                {
                    string? previous = new DirectoryInfo(directoryPath).EnumerateFiles()
                        .Where(file => LogFolderQuota.IsSystemLogFile(file.Name) && !LogFolderQuota.IsProtected(file.FullName))
                        .OrderByDescending(file => file.LastWriteTimeUtc)
                        .Select(file => file.FullName).FirstOrDefault();
                    if (previous is not null) _completedProtection = LogFolderQuota.Protect(previous);
                }
                EnforceQuota();
            }
        }
        catch
        {
            _writer?.Dispose();
            _writer = null;
            _activeProtection?.Dispose();
            _completedProtection?.Dispose();
        }
    }

    public string FilePath => _activePath;

    public bool IsAvailable => _writer is not null;

    public static void PruneDirectory(
        string directoryPath,
        string searchPattern = "ducom-*.log*",
        int retainedFileCount = 20,
        TimeSpan? maximumAge = null,
        long maximumTotalBytes = 100L * 1024 * 1024)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(searchPattern);
        ArgumentOutOfRangeException.ThrowIfNegative(retainedFileCount);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumTotalBytes);
        if (!Directory.Exists(directoryPath))
        {
            return;
        }

        try
        {
            lock (LogFolderQuota.SyncRoot)
            {
                DateTime cutoffUtc = DateTime.UtcNow - (maximumAge ?? TimeSpan.FromDays(30));
                FileInfo[] files = [.. new DirectoryInfo(directoryPath)
                    .EnumerateFiles(searchPattern, SearchOption.TopDirectoryOnly)
                    .Where(file => LogFolderQuota.IsSystemLogFile(file.Name))
                    .OrderByDescending(file => file.LastWriteTimeUtc)];
                long retainedBytes = 0;
                for (int index = 0; index < files.Length; index++)
                {
                    FileInfo file = files[index];
                    if (LogFolderQuota.IsProtected(file.FullName)) continue;
                    bool retain = index < retainedFileCount &&
                        file.LastWriteTimeUtc >= cutoffUtc &&
                        retainedBytes + file.Length <= maximumTotalBytes;
                    if (retain)
                    {
                        retainedBytes += file.Length;
                    }
                    else
                    {
                        try { file.Delete(); }
                        catch (IOException) { /* Another process may still own this log. */ }
                        catch (UnauthorizedAccessException) { /* Continue with other candidates. */ }
                    }
                }
            }
        }
        catch
        {
            // Cleanup failure must not prevent diagnostic logging from starting.
        }
    }

    public void Information(string message) => Write("INFO", message, null);

    public void Warning(string message, Exception? exception = null) => Write("WARN", message, exception);

    public void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    public void Dispose()
    {
        lock (_syncRoot)
        {
            _writer?.Dispose();
            _writer = null;
            _activeProtection?.Dispose();
            _completedProtection?.Dispose();
        }
    }

    private void Write(string level, string message, Exception? exception)
    {
        if (_writer is null)
        {
            return;
        }

        try
        {
            lock (_syncRoot)
            {
                RotateDuringWriteIfRequired();
                if (_writer is null)
                {
                    return;
                }

                _writer.Write(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture));
                _writer.Write(" [");
                _writer.Write(level);
                _writer.Write("] [T");
                _writer.Write(Environment.CurrentManagedThreadId.ToString(CultureInfo.InvariantCulture));
                _writer.Write("] ");
                _writer.WriteLine(message);

                if (exception is not null)
                {
                    _writer.WriteLine(exception);
                }
            }
        }
        catch
        {
            // Diagnostic logging must never become an application failure source.
        }
    }

    private StreamWriter OpenWriter() => new(
        new FileStream(_activePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
    {
        AutoFlush = true,
    };

    private void RotateDuringWriteIfRequired()
    {
        if (_writer is null || _writer.BaseStream.Length < _maximumFileBytes)
        {
            return;
        }

        lock (LogFolderQuota.SyncRoot)
        {
            _writer.Dispose();
            _writer = null;
            RotateIfRequired();
            _writer = OpenWriter();
            EnforceQuota();
        }
    }

    private void RotateIfRequired()
    {
        FileInfo activeFile = new(_activePath);
        if (!activeFile.Exists || activeFile.Length < _maximumFileBytes)
        {
            return;
        }

        if (_retainedFileCount == 0)
        {
            File.Delete(_activePath);
            return;
        }

        string oldestPath = $"{_activePath}.{_retainedFileCount}";
        if (File.Exists(oldestPath))
        {
            File.Delete(oldestPath);
        }

        for (int index = _retainedFileCount - 1; index >= 1; index--)
        {
            string source = $"{_activePath}.{index}";
            if (File.Exists(source))
            {
                File.Move(source, $"{_activePath}.{index + 1}");
            }
        }

        File.Move(_activePath, $"{_activePath}.1");
        _completedProtection?.Dispose();
        _completedProtection = LogFolderQuota.Protect($"{_activePath}.1");
    }

    private void EnforceQuota() => LogFolderQuota.Enforce(
        Path.GetDirectoryName(_activePath)!, _folderQuotaBytes, LogFolderQuota.IsSystemLogFile);
}
