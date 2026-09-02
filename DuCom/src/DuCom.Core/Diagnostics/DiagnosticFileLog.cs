using System.Globalization;
using System.Text;

namespace DuCom.Core.Diagnostics;

public sealed class DiagnosticFileLog : IDisposable
{
    private readonly object _syncRoot = new();
    private readonly string _activePath;
    private readonly long _maximumFileBytes;
    private readonly int _retainedFileCount;
    private StreamWriter? _writer;

    public DiagnosticFileLog(
        string directoryPath,
        string fileName = "ducom.log",
        long maximumFileBytes = 5 * 1024 * 1024,
        int retainedFileCount = 3)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumFileBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(retainedFileCount);

        _activePath = Path.Combine(directoryPath, fileName);
        _maximumFileBytes = maximumFileBytes;
        _retainedFileCount = retainedFileCount;

        try
        {
            Directory.CreateDirectory(directoryPath);
            RotateIfRequired();
            _writer = new StreamWriter(
                new FileStream(_activePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                AutoFlush = true,
            };
        }
        catch
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    public string FilePath => _activePath;

    public void Information(string message) => Write("INFO", message, null);

    public void Warning(string message, Exception? exception = null) => Write("WARN", message, exception);

    public void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    public void Dispose()
    {
        lock (_syncRoot)
        {
            _writer?.Dispose();
            _writer = null;
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
    }
}
