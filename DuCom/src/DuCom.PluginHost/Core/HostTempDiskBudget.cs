namespace DuCom.PluginHost.Core;

/// <summary>Atomic disk reservation shared by host-owned plugin output and snapshot staging.</summary>
public sealed class HostTempDiskBudget
{
    private readonly object _gate = new();
    private readonly Dictionary<string, long> _pluginReservedBytes = new(StringComparer.Ordinal);
    private long _reservedBytes;

    public HostTempDiskBudget(long limitBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limitBytes);
        LimitBytes = limitBytes;
    }

    public long LimitBytes { get; }
    public long ReservedBytes => Interlocked.Read(ref _reservedBytes);

    public long GetPluginReservedBytes(string pluginId)
    {
        ArgumentException.ThrowIfNullOrEmpty(pluginId);
        lock (_gate)
        {
            return _pluginReservedBytes.GetValueOrDefault(pluginId);
        }
    }

    public bool TryReserve(string pluginId, long pluginLimitBytes, long bytes)
    {
        ArgumentException.ThrowIfNullOrEmpty(pluginId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pluginLimitBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        lock (_gate)
        {
            long hostCurrent = _reservedBytes;
            long pluginCurrent = _pluginReservedBytes.GetValueOrDefault(pluginId);
            if (bytes > LimitBytes - hostCurrent || bytes > pluginLimitBytes - pluginCurrent) return false;
            _reservedBytes = hostCurrent + bytes;
            _pluginReservedBytes[pluginId] = pluginCurrent + bytes;
            return true;
        }
    }

    public void Release(string pluginId, long bytes)
    {
        ArgumentException.ThrowIfNullOrEmpty(pluginId);
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        lock (_gate)
        {
            long pluginRemaining = _pluginReservedBytes.GetValueOrDefault(pluginId) - bytes;
            long hostRemaining = _reservedBytes - bytes;
            if (pluginRemaining < 0 || hostRemaining < 0)
                throw new InvalidOperationException("Host temp disk reservation underflow.");
            _reservedBytes = hostRemaining;
            if (pluginRemaining == 0) _pluginReservedBytes.Remove(pluginId);
            else _pluginReservedBytes[pluginId] = pluginRemaining;
        }
    }

    public bool TryReserve(long bytes) => TryReserve("__legacy__", long.MaxValue, bytes);

    public void Release(long bytes) => Release("__legacy__", bytes);
}
