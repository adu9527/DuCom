using System.Collections.Concurrent;

namespace DuCom.PluginHost.Diagnostics;

public enum PluginLogLevel
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// Per-plugin rate-limited diagnostic log: bounded file on disk plus a bounded in-memory ring
/// for the manager UI. Duplicate consecutive messages merge with a counter; excess is dropped
/// and counted, never blocking the caller.
/// </summary>
public sealed class PluginDiagnosticsLog
{
    private const int MaximumFileBytes = 2 * 1024 * 1024;
    private const int RingCapacity = 256;
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(60);

    private readonly string _path;
    private readonly object _gate = new();
    private readonly ConcurrentQueue<(DateTimeOffset Timestamp, PluginLogLevel Level, string Message)> _ring = new();
    private readonly Queue<long> _windowTimestamps = new();
    private string _lastMessage = string.Empty;
    private int _lastMessageRepeat;
    private int _dropped;
    private int _allowedPerWindow = 60;

    public PluginDiagnosticsLog(string path, string pluginId)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        _path = path;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        }
        catch (Exception)
        {
        }
    }

    public IReadOnlyList<(DateTimeOffset Timestamp, PluginLogLevel Level, string Message)> Snapshot()
    {
        return [.. _ring];
    }

    public int DroppedMessages => _dropped;

    public void Write(PluginLogLevel level, string message)
    {
        ArgumentException.ThrowIfNullOrEmpty(message);
        if (message.Length > 2000)
        {
            message = message[..2000];
        }

        lock (_gate)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long windowStart = now - (long)Window.TotalMilliseconds;
            while (_windowTimestamps.Count > 0 && _windowTimestamps.Peek() < windowStart)
            {
                _windowTimestamps.Dequeue();
            }

            if (_windowTimestamps.Count >= _allowedPerWindow)
            {
                _dropped++;
                return;
            }

            _windowTimestamps.Enqueue(now);
            if (string.Equals(_lastMessage, message, StringComparison.Ordinal))
            {
                _lastMessageRepeat++;
                if (_lastMessageRepeat % 10 != 0)
                {
                    return;
                }

                message = $"{message} (x{_lastMessageRepeat + 1})";
            }
            else
            {
                _lastMessage = message;
                _lastMessageRepeat = 0;
            }

            _ring.Enqueue((DateTimeOffset.UtcNow, level, message));
            while (_ring.Count > RingCapacity && _ring.TryDequeue(out _))
            {
            }

            try
            {
                FileInfo existing = new(_path);
                if (existing.Exists && existing.Length > MaximumFileBytes)
                {
                    File.Move(_path, _path + ".1", overwrite: true);
                }

                File.AppendAllText(_path, $"{DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
            }
            catch (Exception)
            {
            }
        }
    }
}

