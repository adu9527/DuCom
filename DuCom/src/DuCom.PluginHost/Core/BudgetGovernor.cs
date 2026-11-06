using System.Diagnostics;

namespace DuCom.PluginHost.Core;

public sealed record BudgetGovernorConfig
{
    /// <summary>Host budget plus the plugin pool. Exceeding this starts emergency governance.</summary>
    public long TotalBudgetBytes { get; init; } = HostBudgetDefault + PluginPoolDefault;

    /// <summary>Warning line at 80% of the total budget.</summary>
    public long WarningThresholdBytes { get; init; } = 1_000_000_000;

    /// <summary>
    /// What the host itself is allowed to use. Above this line the incident belongs to the
    /// host: caches are shed and plugins are left running.
    /// </summary>
    public long HostBudgetBytes { get; init; } = HostBudgetDefault;

    /// <summary>Hard per-process quota for ordinary plugins (job object limit).</summary>
    public long PerPluginQuotaBytes { get; init; } = 250_000_000;

    /// <summary>Hard per-process quota for the BES flashing plugin.</summary>
    public long BesPluginQuotaBytes { get; init; } = 200_000_000;

    /// <summary>Group budget shared by every plugin except the BES one.</summary>
    public long StandardPluginPoolBytes { get; init; } = 250_000_000;

    /// <summary>The BES flashing plugin gets its own quota instead of the shared pool.</summary>
    public string BesPluginId { get; init; } = "internal.ducom.bes-download";

    /// <summary>
    /// Headroom reserved when admitting a new plugin, so activation never eats the last
    /// few megabytes of a group budget.
    /// </summary>
    public long ActivationReserveBytes { get; init; } = 80_000_000;

    public int SampleIntervalMilliseconds { get; init; } = 1000;
    public long HostTempDiskQuotaBytes { get; init; } = 1_000_000_000;

    /// <summary>
    /// Minimum spacing between two emergency stops, including across recovery cycles.
    /// Guards against a burst of samples terminating several healthy plugins in a row.
    /// </summary>
    public int EmergencyStopCooldownMilliseconds { get; init; } = 15_000;

    /// <summary>
    /// Growth observed in a single sample interval that counts as evidence of an
    /// abnormal plugin. Ordinary workers move by a few MB per second at most.
    /// </summary>
    public long WorkerGrowthEvidenceBytes { get; init; } = 32L * 1024 * 1024;

    /// <summary>Total plugin pool: the BES quota plus the ordinary plugins' shared pool.</summary>
    public long PluginPoolBytes => BesPluginQuotaBytes + StandardPluginPoolBytes;

    private const long HostBudgetDefault = 800_000_000;
    private const long PluginPoolDefault = 450_000_000;
}

public sealed record PluginBudgetSample
{
    public required long HostPrivateBytes { get; init; }
    public required long TotalPrivateBytes { get; init; }
    public required long ManagedHeapBytes { get; init; }
    public required long ManagedHeapSizeBytes { get; init; }
    public required long ManagedFragmentedBytes { get; init; }
    public required int ProcessThreadCount { get; init; }
    public required int ThreadPoolThreadCount { get; init; }
    public required long ThreadPoolPendingWorkItemCount { get; init; }
    public required IReadOnlyList<(string PluginId, uint Pid, long PrivateBytes)> Workers { get; init; }

    /// <summary>Private bytes owned by the BES flashing plugin.</summary>
    public long BesPluginBytes { get; init; }

    /// <summary>Private bytes owned by every ordinary plugin together.</summary>
    public long StandardPluginBytes { get; init; }
}

/// <summary>Why the governor picked a worker for an emergency stop.</summary>
public enum BudgetStopReason
{
    /// <summary>No violation was proven; the largest worker was chosen to protect the core.</summary>
    LargestWorker,

    /// <summary>The worker exceeded its own per-process quota.</summary>
    QuotaExceeded,

    /// <summary>The worker's group or the whole plugin pool exceeded its share.</summary>
    GroupQuotaExceeded,

    /// <summary>The worker grew faster than the evidence threshold within one sample interval.</summary>
    AbnormalGrowth,
}

public sealed record BudgetStopRequest
{
    public required string PluginId { get; init; }
    public required uint Pid { get; init; }
    public required long PrivateBytes { get; init; }
    public required PluginBudgetSample Sample { get; init; }
    public BudgetStopReason Reason { get; init; } = BudgetStopReason.LargestWorker;
    public long GrowthBytes { get; init; }
}

/// <summary>The split budgets that govern one sample. Kept separate from the mutable governor for tests.</summary>
internal readonly record struct BudgetLimits(
    long HostBudgetBytes,
    long PerPluginQuotaBytes,
    long BesPluginQuotaBytes,
    long StandardPluginPoolBytes,
    long PluginPoolBytes,
    long GrowthEvidenceBytes,
    string BesPluginId)
{
    public bool IsBes(string pluginId) => string.Equals(pluginId, BesPluginId, StringComparison.Ordinal);
}

/// <summary>A single emergency-stop decision over one sample. Pure data for tests.</summary>
internal readonly record struct BudgetStopDecision(
    string PluginId,
    uint Pid,
    long PrivateBytes,
    long GrowthBytes,
    BudgetStopReason Reason);

/// <summary>
/// Tool-wide memory budget accounting over Windows private committed bytes: the host and every
/// plugin worker. Sampling drives warning and emergency governance; per-process hard caps come
/// from job objects at spawn time. Sampling alone never proves a hard instantaneous ceiling, and
/// this class never kills the host to fake one.
/// </summary>
public sealed class BudgetGovernor : IDisposable
{
    private readonly BudgetGovernorConfig _config;
    private readonly object _gate = new();
    private readonly Dictionary<uint, string> _workers = new();
    private readonly HashSet<uint> _memoryMonitorHelpers = [];
    private readonly Dictionary<uint, long> _lastBytes = new();
    private Timer? _timer;
    private bool _warningActive;
    private bool _restrictionActive;
    private long _lastHostBytes = -1;
    private long _nextEmergencyStopTimestamp;
    private int _hostProcessId;

    public BudgetGovernorConfig Configuration => _config;

    public event Action<PluginBudgetSample>? Warning;

    public event Action<PluginBudgetSample>? Sampled;

    public event Action<BudgetStopRequest>? EmergencyStop;

    /// <summary>
    /// Raised instead of <see cref="EmergencyStop"/> when the host itself is over its own
    /// budget and no plugin group is over its share. Plugins are left alone; the host is
    /// expected to shed its own caches. Raised once per over-budget episode.
    /// </summary>
    public event Action<PluginBudgetSample>? HostPressure;

    public event Action<PluginBudgetSample>? Recovered;

    public event Action<PluginBudgetSample>? RestrictionRecovered;

    public PluginBudgetSample? LastSample { get; private set; }

    public BudgetGovernor(BudgetGovernorConfig config)
    {
        _config = config;
        if (_config.WarningThresholdBytes >= _config.TotalBudgetBytes)
        {
            throw new ArgumentException("The warning threshold must stay below the total budget.");
        }
        if (_config.PluginPoolBytes > _config.TotalBudgetBytes)
        {
            throw new ArgumentException("The plugin pool cannot exceed the total budget.");
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

    // Both sandbox workers and broker-launched helpers use single-process jobs with
    // no breakaway. Helpers are siblings of workers, not discoverable as worker children.
    // Keep this UI membership separate from private-commit budget governance.
    public uint[] GetMemoryMonitorProcessIds()
    {
        lock (_gate) return [.. _workers.Keys.Concat(_memoryMonitorHelpers).Distinct()];
    }

    internal void RegisterMemoryMonitorHelper(uint pid)
    {
        lock (_gate) _memoryMonitorHelpers.Add(pid);
    }

    internal void UnregisterMemoryMonitorHelper(uint pid)
    {
        lock (_gate) _memoryMonitorHelpers.Remove(pid);
    }

    public bool IsBesPlugin(string pluginId) => _config.BesPluginId.Length > 0
        && string.Equals(pluginId, _config.BesPluginId, StringComparison.Ordinal);

    /// <summary>Hard per-process quota handed to the job object for this plugin.</summary>
    public long GetPluginQuota(string pluginId) =>
        IsBesPlugin(pluginId) ? _config.BesPluginQuotaBytes : _config.PerPluginQuotaBytes;

    public bool CanActivatePlugins() => CanActivatePlugins(null);

    /// <summary>
    /// Admission control. A plugin may start only when its own group still has room for the
    /// activation reserve; the BES plugin draws from its own quota, everything else from the
    /// shared pool.
    /// </summary>
    public bool CanActivatePlugins(string? pluginId)
    {
        PluginBudgetSample? sample = LastSample;
        if (sample is null)
        {
            return true;
        }
        if (sample.TotalPrivateBytes >= _config.TotalBudgetBytes)
        {
            return false;
        }
        if (string.IsNullOrEmpty(pluginId))
        {
            return true;
        }

        bool bes = IsBesPlugin(pluginId);
        long used = bes ? sample.BesPluginBytes : sample.StandardPluginBytes;
        long budget = bes ? _config.BesPluginQuotaBytes : _config.StandardPluginPoolBytes;
        return used + _config.ActivationReserveBytes <= budget;
    }

    public bool CanRecoverPlugins()
    {
        PluginBudgetSample? sample = LastSample;
        return sample is null || sample.TotalPrivateBytes < _config.WarningThresholdBytes;
    }

    public PluginBudgetSample Sample()
    {
        ProcessMemorySnapshot hostSnapshot = GetProcessMemorySnapshot(_hostProcessId);
        long host = hostSnapshot.PrivateBytes;
        GCMemoryInfo gc = GC.GetGCMemoryInfo();
        List<(string, uint, long)> workers = [];
        Dictionary<uint, long> previousBytes;
        long previousHost;
        uint[] pids;
        lock (_gate)
        {
            pids = [.. _workers.Keys];
            previousBytes = new Dictionary<uint, long>(_lastBytes);
            previousHost = _lastHostBytes;
        }

        long total = host;
        long besBytes = 0;
        long standardBytes = 0;
        foreach (uint pid in pids)
        {
            long bytes = GetPrivateBytes((int)pid);
            if (bytes < 0)
            {
                continue;
            }

            string pluginId;
            lock (_gate)
            {
                pluginId = _workers.TryGetValue(pid, out string? registered) ? registered : "?";
                _lastBytes[pid] = bytes;
            }

            workers.Add((pluginId, pid, bytes));
            total += bytes;
            if (IsBesPlugin(pluginId))
            {
                besBytes += bytes;
            }
            else
            {
                standardBytes += bytes;
            }
        }

        PluginBudgetSample sample = new()
        {
            HostPrivateBytes = host,
            TotalPrivateBytes = total,
            ManagedHeapBytes = GC.GetTotalMemory(forceFullCollection: false),
            ManagedHeapSizeBytes = gc.HeapSizeBytes,
            ManagedFragmentedBytes = gc.FragmentedBytes,
            ProcessThreadCount = hostSnapshot.ThreadCount,
            ThreadPoolThreadCount = ThreadPool.ThreadCount,
            ThreadPoolPendingWorkItemCount = ThreadPool.PendingWorkItemCount,
            Workers = workers,
            BesPluginBytes = besBytes,
            StandardPluginBytes = standardBytes,
        };
        LastSample = sample;

        long hostGrowth = host >= 0 && previousHost >= 0 ? host - previousHost : 0;
        lock (_gate) _lastHostBytes = host;

        Sampled?.Invoke(sample);

        if (total >= _config.TotalBudgetBytes)
        {
            bool firstEscalation = !_restrictionActive;
            _restrictionActive = true;
            _warningActive = true;

            (BudgetStopRequest? stop, bool suppressed) = DecideStop(sample, previousBytes, hostGrowth, firstEscalation);
            if (stop is not null) EmergencyStop?.Invoke(stop);
            else if (firstEscalation && !suppressed) HostPressure?.Invoke(sample);
        }
        else
        {
            if (_restrictionActive)
            {
                _restrictionActive = false;
                RestrictionRecovered?.Invoke(sample);
            }

            if (total >= _config.WarningThresholdBytes)
            {
                _warningActive = true;
                Warning?.Invoke(sample);
            }
            else if (_warningActive)
            {
                _warningActive = false;
                Recovered?.Invoke(sample);
            }
        }

        return sample;
    }

    /// <summary>
    /// Applies attribution and de-bouncing to one over-budget sample. Explicit evidence always
    /// wins (own quota, group share, abnormal growth). A host above its own budget never costs
    /// a plugin unless a plugin group is itself over its share.
    /// </summary>
    private (BudgetStopRequest? Stop, bool Suppressed) DecideStop(
        PluginBudgetSample sample,
        IReadOnlyDictionary<uint, long> previousBytes,
        long hostGrowth,
        bool firstEscalation)
    {
        BudgetLimits limits = new(
            _config.HostBudgetBytes,
            _config.PerPluginQuotaBytes,
            _config.BesPluginQuotaBytes,
            _config.StandardPluginPoolBytes,
            _config.PluginPoolBytes,
            _config.WorkerGrowthEvidenceBytes,
            _config.BesPluginId);

        BudgetStopDecision? decision = DecideEmergencyStop(
            sample.HostPrivateBytes,
            sample.Workers,
            previousBytes,
            hostGrowth,
            limits,
            requireEvidence: !firstEscalation);

        if (decision is null)
        {
            return (null, false);
        }

        long now = Environment.TickCount64;
        lock (_gate)
        {
            // A stop inside the cooldown is deferred rather than turned into host pressure:
            // the evidence points at a plugin, we simply refuse to chain stops.
            if (now < _nextEmergencyStopTimestamp)
            {
                return (null, true);
            }

            _nextEmergencyStopTimestamp = now + _config.EmergencyStopCooldownMilliseconds;
        }

        BudgetStopDecision stop = decision.Value;
        return (new BudgetStopRequest
        {
            PluginId = stop.PluginId,
            Pid = stop.Pid,
            PrivateBytes = stop.PrivateBytes,
            GrowthBytes = stop.GrowthBytes,
            Reason = stop.Reason,
            Sample = sample,
        }, false);
    }

    /// <summary>
    /// Chooses which worker, if any, to stop. Order of evidence: own quota, abnormal growth,
    /// group or pool share, then the host. A host above its own budget and not owing the debt
    /// is reported through <see cref="HostPressure"/> instead of sacrificing a plugin.
    /// </summary>
    internal static BudgetStopDecision? DecideEmergencyStop(
        long hostBytes,
        IReadOnlyList<(string PluginId, uint Pid, long PrivateBytes)> workers,
        IReadOnlyDictionary<uint, long> previousBytes,
        long hostGrowthBytes,
        BudgetLimits limits,
        bool requireEvidence)
    {
        if (workers.Count == 0)
        {
            return null;
        }

        BudgetStopDecision? ownQuota = null;
        BudgetStopDecision? growth = null;
        BudgetStopDecision? largest = null;
        BudgetStopDecision? largestBes = null;
        BudgetStopDecision? largestStandard = null;
        long pluginGrowth = 0;
        long besBytes = 0;
        long standardBytes = 0;

        foreach ((string pluginId, uint pid, long privateBytes) in workers)
        {
            bool bes = limits.IsBes(pluginId);
            long workerGrowth = previousBytes.TryGetValue(pid, out long before) ? privateBytes - before : 0;
            pluginGrowth += workerGrowth;
            if (bes) besBytes += privateBytes;
            else standardBytes += privateBytes;

            long ownBudget = bes ? limits.BesPluginQuotaBytes : limits.PerPluginQuotaBytes;
            if (privateBytes >= ownBudget
                && (ownQuota is null || privateBytes - ownBudget > ownQuota.Value.PrivateBytes - ownBudget))
            {
                ownQuota = new(pluginId, pid, privateBytes, workerGrowth, BudgetStopReason.QuotaExceeded);
            }

            if (workerGrowth >= limits.GrowthEvidenceBytes
                && (growth is null || workerGrowth > growth.Value.GrowthBytes))
            {
                growth = new(pluginId, pid, privateBytes, workerGrowth, BudgetStopReason.AbnormalGrowth);
            }

            if (largest is null || privateBytes > largest.Value.PrivateBytes)
            {
                largest = new(pluginId, pid, privateBytes, workerGrowth, BudgetStopReason.LargestWorker);
            }

            if (bes)
            {
                if (largestBes is null || privateBytes > largestBes.Value.PrivateBytes)
                {
                    largestBes = new(pluginId, pid, privateBytes, workerGrowth, BudgetStopReason.GroupQuotaExceeded);
                }
            }
            else if (largestStandard is null || privateBytes > largestStandard.Value.PrivateBytes)
            {
                largestStandard = new(pluginId, pid, privateBytes, workerGrowth, BudgetStopReason.GroupQuotaExceeded);
            }
        }

        if (ownQuota is { } quotaStop)
        {
            return quotaStop;
        }

        if (growth is { } growthStop)
        {
            return growthStop;
        }

        // Group evidence: a plugin group over its own share owes the debt regardless of the
        // host, because the host budget is a separate allowance.
        if (besBytes > limits.BesPluginQuotaBytes && largestBes is { } besStop)
        {
            return besStop;
        }

        if (standardBytes > limits.StandardPluginPoolBytes && largestStandard is { } standardStop)
        {
            return standardStop;
        }

        if (besBytes + standardBytes > limits.PluginPoolBytes && largest is { } poolStop)
        {
            return poolStop;
        }

        // The host owns a separate budget. Above it, pressure belongs to the host.
        if (hostBytes >= limits.HostBudgetBytes)
        {
            return null;
        }

        if (requireEvidence)
        {
            return null;
        }

        return pluginGrowth > hostGrowthBytes ? largest : null;
    }

    public long GetWorkerBytes(uint pid)
    {
        lock (_gate)
        {
            return _lastBytes.TryGetValue(pid, out long bytes) ? bytes : 0;
        }
    }

    private static long GetPrivateBytes(int pid)
        => GetProcessMemorySnapshot(pid).PrivateBytes;

    private static ProcessMemorySnapshot GetProcessMemorySnapshot(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            process.Refresh();
            return new ProcessMemorySnapshot(process.PrivateMemorySize64, process.Threads.Count);
        }
        catch (Exception)
        {
            return new ProcessMemorySnapshot(-1, -1);
        }
    }

    private readonly record struct ProcessMemorySnapshot(long PrivateBytes, int ThreadCount);

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
    }
}
