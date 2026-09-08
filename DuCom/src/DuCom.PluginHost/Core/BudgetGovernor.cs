using System.Diagnostics;

namespace DuCom.PluginHost.Core;

public sealed record BudgetGovernorConfig
{
    public long TotalBudgetBytes { get; init; } = 1_000_000_000;
    public long WarningThresholdBytes { get; init; } = 800_000_000;
    public long CoreReserveBytes { get; init; } = 500_000_000;
    public long PerPluginQuotaBytes { get; init; } = 250_000_000;
    public int SampleIntervalMilliseconds { get; init; } = 1000;
    public long HostTempDiskQuotaBytes { get; init; } = 1_000_000_000;
}

public sealed record PluginBudgetSample
{
    public required long HostPrivateBytes { get; init; }
    public required long TotalPrivateBytes { get; init; }
    public required IReadOnlyList<(string PluginId, uint Pid, long PrivateBytes)> Workers { get; init; }
}

public sealed record BudgetStopRequest
{
    public required string PluginId { get; init; }
    public required uint Pid { get; init; }
    public required long PrivateBytes { get; init; }
    public required PluginBudgetSample Sample { get; init; }
}

/// <summary>
/// Tool-wide memory budget accounting over Windows private committed bytes: the host, every
/// plugin worker, and any managed helper. Sampling drives warning and emergency governance;
/// per-process hard caps come from job objects at spawn time. Sampling alone never proves a
/// hard instantaneous ceiling, and this class never kills the host to fake one.
/// </summary>
public sealed class BudgetGovernor : IDisposable
{
    private readonly BudgetGovernorConfig _config;
    private readonly object _gate = new();
    private readonly Dictionary<uint, string> _workers = new();
    private readonly Dictionary<uint, long> _lastBytes = new();
    private Timer? _timer;
    private bool _warningActive;
    private int _hostProcessId;

    public BudgetGovernorConfig Configuration => _config;

    public event Action<PluginBudgetSample>? Warning;

    public event Action<BudgetStopRequest>? EmergencyStop;

    public event Action<PluginBudgetSample>? Recovered;

    public PluginBudgetSample? LastSample { get; private set; }

    public BudgetGovernor(BudgetGovernorConfig config)
    {
        _config = config;
        if (_config.WarningThresholdBytes >= _config.TotalBudgetBytes)
        {
            throw new ArgumentException("The warning threshold must stay below the total budget.");
        }
    }

    public void Start()
    {
        _hostProcessId = Environment.ProcessId;
        _timer = new Timer(_ => Sample(), null, _config.SampleIntervalMilliseconds, _config.SampleIntervalMilliseconds);
    }

    public void RegisterWorker(string pluginId, uint pid)
    {
        ArgumentException.ThrowIfNullOrEmpty(pluginId);
        lock (_gate)
        {
            _workers[pid] = pluginId;
        }
    }

    public void UnregisterWorker(uint pid)
    {
        lock (_gate)
        {
            _workers.Remove(pid);
            _lastBytes.Remove(pid);
        }
    }

    public bool CanActivatePlugins()
    {
        PluginBudgetSample? sample = LastSample;
        return sample is null || sample.TotalPrivateBytes < _config.WarningThresholdBytes;
    }

    public PluginBudgetSample Sample()
    {
        long host = GetPrivateBytes(_hostProcessId);
        List<(string, uint, long)> workers = [];
        uint[] pids;
        lock (_gate)
        {
            pids = [.. _workers.Keys];
        }

        long total = host;
        foreach (uint pid in pids)
        {
            long bytes = GetPrivateBytes((int)pid);
            if (bytes >= 0)
            {
                workers.Add((_workers.TryGetValue(pid, out string? id) ? id : "?", pid, bytes));
                total += bytes;
                lock (_gate)
                {
                    _lastBytes[pid] = bytes;
                }
            }
        }

        PluginBudgetSample sample = new()
        {
            HostPrivateBytes = host,
            TotalPrivateBytes = total,
            Workers = workers,
        };
        LastSample = sample;

        if (total >= _config.TotalBudgetBytes)
        {
            (string pluginId, uint pid, long bytes) = workers.OrderByDescending(w => w.Item3).FirstOrDefault();
            if (pid != 0)
            {
                EmergencyStop?.Invoke(new BudgetStopRequest
                {
                    PluginId = pluginId,
                    Pid = pid,
                    PrivateBytes = bytes,
                    Sample = sample,
                });
            }
            else if (host >= _config.TotalBudgetBytes)
            {
                Warning?.Invoke(sample);
            }

            _warningActive = true;
        }
        else if (total >= _config.WarningThresholdBytes)
        {
            _warningActive = true;
            Warning?.Invoke(sample);
        }
        else if (_warningActive)
        {
            _warningActive = false;
            Recovered?.Invoke(sample);
        }

        return sample;
    }

    public long GetWorkerBytes(uint pid)
    {
        lock (_gate)
        {
            return _lastBytes.TryGetValue(pid, out long bytes) ? bytes : 0;
        }
    }

    private static long GetPrivateBytes(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            process.Refresh();
            return process.PrivateMemorySize64;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
    }
}
