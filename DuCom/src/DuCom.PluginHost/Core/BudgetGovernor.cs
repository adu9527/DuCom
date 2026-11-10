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

    /// <summary>Aggregate sampled ordinary-plugin quota; also the hard cap of each worker/helper job.</summary>
    public long PerPluginQuotaBytes { get; init; } = 250_000_000;

    /// <summary>Aggregate sampled BES quota; also the hard cap of each BES worker/helper job.</summary>
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
    /// <summary>Worker processes only; bytes remain per-process for existing UI/diagnostics consumers.</summary>
    public required IReadOnlyList<(string PluginId, uint Pid, long PrivateBytes)> Workers { get; init; }

    /// <summary>Helpers (including unconfirmed-exit remnants), counted once in plugin/group/total budgets.</summary>
    public IReadOnlyList<(string PluginId, string ActivationId, uint Pid, long PrivateBytes)> Helpers { get; init; } = [];

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

    /// <summary>The plugin's worker plus all helpers exceeded its aggregate quota.</summary>
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
    /// <summary>The sampled worker generation; stop handlers must match both PID and activation.</summary>
    public string ActivationId { get; init; } = string.Empty;
    /// <summary>Aggregate private bytes of this plugin's worker and all registered helpers.</summary>
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
    private readonly record struct ProcessOwner(string PluginId, string ActivationId);
    private readonly Dictionary<uint, ProcessOwner> _workers = new();
    private readonly Dictionary<uint, ProcessOwner> _memoryMonitorHelpers = new();
    private readonly Func<int, ProcessMemorySnapshot> _readMemory;
    private readonly object _sampleGate = new();
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

    public BudgetGovernor(BudgetGovernorConfig config) : this(config, GetProcessMemorySnapshot)
    {
    }

    // Exercises the real Sample outer flow without real processes, timers or hardware.
    internal BudgetGovernor(BudgetGovernorConfig config, Func<int, ProcessMemorySnapshot> readMemory)
    {
        _config = config;
        _readMemory = readMemory ?? throw new ArgumentNullException(nameof(readMemory));
        _hostProcessId = Environment.ProcessId;
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

    public void RegisterWorker(string pluginId, uint pid) => RegisterWorker(pluginId, pid, string.Empty);

    public void RegisterWorker(string pluginId, uint pid, string activationId)
    {
        ArgumentException.ThrowIfNullOrEmpty(pluginId);
        lock (_gate)
        {
            ProcessOwner owner = new(pluginId, activationId);
            if (!_workers.TryGetValue(pid, out ProcessOwner previous) || previous != owner)
                _lastBytes.Remove(pid);
            _workers[pid] = owner;
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

    // Helpers are siblings, not discoverable worker children. The same owned membership
    // drives both the UI process list and private-commit budget accounting.
    public uint[] GetMemoryMonitorProcessIds()
    {
        lock (_gate) return [.. _workers.Keys.Concat(_memoryMonitorHelpers.Keys).Distinct()];
    }

    internal void RegisterMemoryMonitorHelper(string pluginId, string activationId, uint pid)
    {
        ArgumentException.ThrowIfNullOrEmpty(pluginId);
        ArgumentException.ThrowIfNullOrEmpty(activationId);
        lock (_gate)
        {
            ProcessOwner owner = new(pluginId, activationId);
            if (!_memoryMonitorHelpers.TryGetValue(pid, out ProcessOwner previous) || previous != owner)
                _lastBytes.Remove(pid);
            _memoryMonitorHelpers[pid] = owner;
        }
    }

    // Called only after the retained process handle has signalled actual exit.
    internal void UnregisterMemoryMonitorHelper(uint pid)
    {
        lock (_gate)
        {
            _memoryMonitorHelpers.Remove(pid);
            _lastBytes.Remove(pid);
        }
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
        // Timer callbacks can overlap; keep baseline, cooldown and episode transitions ordered.
        lock (_sampleGate) return SampleCore();
    }

    private PluginBudgetSample SampleCore()
    {
        ProcessMemorySnapshot hostSnapshot = _readMemory(_hostProcessId);
        long host = Math.Max(0, hostSnapshot.PrivateBytes);
        GCMemoryInfo gc = GC.GetGCMemoryInfo();
        List<(string PluginId, uint Pid, long PrivateBytes)> workers = [];
        List<(string PluginId, string ActivationId, uint Pid, long PrivateBytes)> helpers = [];
        Dictionary<uint, ProcessOwner> workerOwners;
        Dictionary<uint, ProcessOwner> helperOwners;
        Dictionary<uint, long> previousBytes;
        long previousHost;
        lock (_gate)
        {
            workerOwners = new(_workers);
            helperOwners = new(_memoryMonitorHelpers);
            previousBytes = new(_lastBytes);
            previousHost = _lastHostBytes;
        }

        Dictionary<string, long> pluginBytes = new(StringComparer.Ordinal);
        Dictionary<string, long> previousPluginBytes = new(StringComparer.Ordinal);
        foreach ((uint pid, ProcessOwner owner) in workerOwners.Concat(helperOwners.Where(pair => !workerOwners.ContainsKey(pair.Key))))
        {
            long bytes = _readMemory((int)pid).PrivateBytes;
            // A failed read is not proof of exit. Retain last-known usage until the owner
            // confirms exit through its retained handle and unregisters the process.
            if (bytes < 0) bytes = previousBytes.GetValueOrDefault(pid);
            lock (_gate)
            {
                Dictionary<uint, ProcessOwner> current = workerOwners.ContainsKey(pid) ? _workers : _memoryMonitorHelpers;
                if (!current.TryGetValue(pid, out ProcessOwner registered) || registered != owner) continue;
                _lastBytes[pid] = bytes;
            }
            if (workerOwners.ContainsKey(pid)) workers.Add((owner.PluginId, pid, bytes));
            else helpers.Add((owner.PluginId, owner.ActivationId, pid, bytes));
            pluginBytes[owner.PluginId] = pluginBytes.GetValueOrDefault(owner.PluginId) + bytes;
            // Newly seen processes have no growth evidence on their first sample.
            previousPluginBytes[owner.PluginId] = previousPluginBytes.GetValueOrDefault(owner.PluginId)
                + previousBytes.GetValueOrDefault(pid, bytes);
        }

        long besBytes = pluginBytes.Where(pair => IsBesPlugin(pair.Key)).Sum(pair => pair.Value);
        long standardBytes = pluginBytes.Where(pair => !IsBesPlugin(pair.Key)).Sum(pair => pair.Value);
        long total = host + besBytes + standardBytes;
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
            Helpers = helpers,
            BesPluginBytes = besBytes,
            StandardPluginBytes = standardBytes,
        };
        LastSample = sample;
        long hostGrowth = previousHost >= 0 ? host - previousHost : 0;
        lock (_gate) _lastHostBytes = host;

        // Decision rows are plugin aggregates, not extra independent helper workers. The
        // representative PID/activation is captured now, before event handlers can restart it.
        List<(string PluginId, uint Pid, long PrivateBytes)> aggregates = [];
        Dictionary<uint, long> previousAggregates = new();
        Dictionary<uint, ProcessOwner> stopOwners = new();
        foreach ((string pluginId, long bytes) in pluginBytes)
        {
            uint pid = workers.LastOrDefault(worker => worker.PluginId == pluginId).Pid;
            if (pid != 0 && bytes >= GetPluginQuota(pluginId))
            {
                ProcessOwner currentOwner = workerOwners[pid];
                long currentActivationBytes = workers.Where(worker => worker.PluginId == pluginId
                        && workerOwners[worker.Pid].ActivationId == currentOwner.ActivationId).Sum(worker => worker.PrivateBytes)
                    + helpers.Where(helper => helper.PluginId == pluginId && helper.ActivationId == currentOwner.ActivationId)
                        .Sum(helper => helper.PrivateBytes);
                // Old helper debt stays charged, but cannot turn an old activation's stop
                // into a stop of a healthy replacement activation.
                if (currentActivationBytes < GetPluginQuota(pluginId)
                    && helpers.Any(helper => helper.PluginId == pluginId && helper.ActivationId != currentOwner.ActivationId))
                    pid = 0;
            }
            aggregates.Add((pluginId, pid, bytes));
            if (pid != 0)
            {
                previousAggregates[pid] = previousPluginBytes[pluginId];
                stopOwners[pid] = workerOwners[pid];
            }
        }

        Sampled?.Invoke(sample);
        bool totalExceeded = total >= _config.TotalBudgetBytes;
        bool quotaExceeded = pluginBytes.Any(pair => pair.Value >= GetPluginQuota(pair.Key))
            || besBytes > _config.BesPluginQuotaBytes || standardBytes > _config.StandardPluginPoolBytes
            || besBytes + standardBytes > _config.PluginPoolBytes;
        if (totalExceeded || quotaExceeded)
        {
            bool firstEscalation = !_restrictionActive;
            _restrictionActive = true;
            _warningActive = true;
            (BudgetStopRequest? stop, bool suppressed) = DecideStop(
                sample, aggregates, previousAggregates, stopOwners, hostGrowth, firstEscalation, totalExceeded);
            if (stop is not null) EmergencyStop?.Invoke(stop);
            else if (totalExceeded && firstEscalation && !suppressed && !quotaExceeded) HostPressure?.Invoke(sample);
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
        IReadOnlyList<(string PluginId, uint Pid, long PrivateBytes)> aggregates,
        IReadOnlyDictionary<uint, long> previousBytes,
        IReadOnlyDictionary<uint, ProcessOwner> stopOwners,
        long hostGrowth,
        bool firstEscalation,
        bool totalExceeded)
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
            aggregates,
            previousBytes,
            hostGrowth,
            limits,
            requireEvidence: !firstEscalation,
            allowGrowthEvidence: totalExceeded);

        if (decision is null || decision.Value.Pid == 0
            || !stopOwners.TryGetValue(decision.Value.Pid, out ProcessOwner owner))
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

            if (!_workers.TryGetValue(decision.Value.Pid, out ProcessOwner current) || current != owner)
                return (null, true);
            _nextEmergencyStopTimestamp = now + _config.EmergencyStopCooldownMilliseconds;
        }

        BudgetStopDecision stop = decision.Value;
        return (new BudgetStopRequest
        {
            PluginId = stop.PluginId,
            Pid = stop.Pid,
            ActivationId = owner.ActivationId,
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
        bool requireEvidence,
        bool allowGrowthEvidence = true)
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

            // Orphan remnants still owe group/pool bytes, but have no current worker to
            // terminate. Do not let one hide another actionable violating plugin.
            if (pid == 0) continue;
            long ownBudget = bes ? limits.BesPluginQuotaBytes : limits.PerPluginQuotaBytes;
            if (privateBytes >= ownBudget
                && (ownQuota is null || privateBytes - ownBudget > ownQuota.Value.PrivateBytes
                    - (limits.IsBes(ownQuota.Value.PluginId) ? limits.BesPluginQuotaBytes : limits.PerPluginQuotaBytes)))
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

        if (allowGrowthEvidence && growth is { } growthStop)
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

        return allowGrowthEvidence && pluginGrowth > hostGrowthBytes ? largest : null;
    }

    public long GetWorkerBytes(uint pid)
    {
        lock (_gate)
        {
            return _lastBytes.TryGetValue(pid, out long bytes) ? bytes : 0;
        }
    }

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

    internal readonly record struct ProcessMemorySnapshot(long PrivateBytes, int ThreadCount);

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
    }
}
