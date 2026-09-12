using System.Text.Json;
using DuCom.Core.Persistence;
using DuCom.PluginHost.Diagnostics;

namespace DuCom.PluginHost.Core;

public enum HostTempResourceKind
{
    OutputFile,
    SnapshotDirectory,
    PublicationFile,
}

public sealed record HostTempResourceRecord
{
    public required string ResourceId { get; init; }
    public required string PluginId { get; init; }
    public required string ActivationId { get; init; }
    public required HostTempResourceKind Kind { get; init; }
    public required string Path { get; init; }
    public required long PluginLimitBytes { get; init; }
    public long ReservedBytes { get; set; }
    public bool CleanupPending { get; set; }
}

public sealed class HostTempResourceLedger : IDisposable
{
    private readonly string _ledgerPath;
    private readonly string _hostTempRoot;
    private readonly FileStream _liveLock;
    private readonly HostTempDiskBudget _budget;
    private readonly object _gate = new();
    private readonly Dictionary<string, HostTempResourceRecord> _records = new(StringComparer.Ordinal);
    private readonly Timer _retryTimer;
    private readonly List<FileStream> _abandonedLocks = [];
    private readonly Action<string, string> _writeLedger;
    private bool _disposed;

    public HostTempResourceLedger(string tempRoot, string hostRunId, HostTempDiskBudget budget)
        : this(tempRoot, hostRunId, budget, AtomicFileStore.WriteAllText)
    {
    }

    internal HostTempResourceLedger(string tempRoot, string hostRunId, HostTempDiskBudget budget, Action<string, string> writeLedger)
    {
        _writeLedger = writeLedger;
        string ledgerRoot = Path.Combine(tempRoot, "Ledgers");
        Directory.CreateDirectory(ledgerRoot);
        _hostTempRoot = Path.GetFullPath(tempRoot) + Path.DirectorySeparatorChar;
        _budget = budget;
        _ledgerPath = Path.Combine(ledgerRoot, hostRunId + ".json");
        _liveLock = new FileStream(Path.Combine(ledgerRoot, hostRunId + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            CleanupAbandoned(ledgerRoot, hostRunId);
        }
        catch
        {
            foreach (HostTempResourceRecord record in _records.Values)
                _budget.Release(record.PluginId, record.ReservedBytes);
            foreach (FileStream abandoned in _abandonedLocks) abandoned.Dispose();
            _liveLock.Dispose();
            throw;
        }
        _retryTimer = new Timer(_ => RetryPendingCleanups(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    public IReadOnlyList<HostTempResourceRecord> Snapshot()
    {
        lock (_gate) return [.. _records.Values.Select(Clone)];
    }

    public void RegisterIntent(HostTempResourceRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        Validate(record);
        if (record.ReservedBytes != 0)
            throw new ArgumentException("New resource intents must not contain reservations.", nameof(record));
        lock (_gate)
        {
            if (!_records.TryAdd(record.ResourceId, Clone(record)))
                throw new InvalidOperationException("Host temp resource id is already registered.");
            try { Save(); }
            catch
            {
                _records.Remove(record.ResourceId);
                throw;
            }
        }
    }

    public bool TryReserve(string resourceId, long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        lock (_gate)
        {
            HostTempResourceRecord record = Get(resourceId);
            if (!_budget.TryReserve(record.PluginId, record.PluginLimitBytes, bytes)) return false;
            long previous = record.ReservedBytes;
            try
            {
                record.ReservedBytes = checked(record.ReservedBytes + bytes);
                Save();
                return true;
            }
            catch
            {
                record.ReservedBytes = previous;
                _budget.Release(record.PluginId, bytes);
                throw;
            }
        }
    }

    public void ReleaseReservation(string resourceId, long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        lock (_gate)
        {
            HostTempResourceRecord record = Get(resourceId);
            if (bytes > record.ReservedBytes) throw new InvalidOperationException("Resource reservation underflow.");
            record.ReservedBytes -= bytes;
            try { Save(); }
            catch
            {
                record.ReservedBytes += bytes;
                throw;
            }
            _budget.Release(record.PluginId, bytes);
        }
    }

    public bool TryCleanup(string resourceId)
    {
        lock (_gate)
        {
            if (!_records.TryGetValue(resourceId, out HostTempResourceRecord? record)) return true;
            record.CleanupPending = true;
            Save();
            if (!TryDelete(record)) return false;
            _records.Remove(resourceId);
            try { Save(); }
            catch
            {
                _records.Add(resourceId, record);
                throw;
            }
            if (record.ReservedBytes > 0) _budget.Release(record.PluginId, record.ReservedBytes);
            return true;
        }
    }

    public void RetryPendingCleanups()
    {
        lock (_gate)
        {
            if (_disposed) return;
            foreach (HostTempResourceRecord record in _records.Values.ToArray())
            {
                if (!record.CleanupPending) continue;
                try { TryCleanup(record.ResourceId); }
                catch (Exception)
                {
                    // Keep the charged record for a later retry; timer exceptions must not escape.
                }
            }
        }
    }

    private void CleanupAbandoned(string ledgerRoot, string currentHostRunId)
    {
        // Serialize adoption so concurrent starters cannot split ownership of duplicate source ledgers.
        using FileStream recoveryLock = new(Path.Combine(ledgerRoot, ".recovery-gate"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        List<string> sources = [];
        Dictionary<string, HostTempResourceRecord> recovered = new(StringComparer.Ordinal);
        foreach (string ledger in Directory.EnumerateFiles(ledgerRoot, "*.json"))
        {
            string runId = Path.GetFileNameWithoutExtension(ledger);
            if (runId == currentHostRunId) continue;
            string lockPath = Path.Combine(ledgerRoot, runId + ".lock");
            FileStream abandoned;
            try { abandoned = new(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { continue; }
            // Retain source locks even if deletion fails, so another live host cannot adopt a stale copy.
            _abandonedLocks.Add(abandoned);
            HostTempResourceRecord[] records;
            try
            {
                records = JsonSerializer.Deserialize<HostTempResourceRecord[]>(File.ReadAllText(ledger)) ?? [];
            }
            catch (JsonException)
            {
                continue;
            }

            foreach (HostTempResourceRecord record in records)
            {
                Validate(record);
                if (recovered.TryGetValue(record.ResourceId, out HostTempResourceRecord? existing))
                {
                    if (existing.PluginId != record.PluginId || existing.ActivationId != record.ActivationId
                        || existing.Kind != record.Kind
                        || !Path.GetFullPath(existing.Path).Equals(Path.GetFullPath(record.Path), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Conflicting host temp resource registrations.");
                    existing.ReservedBytes = Math.Max(existing.ReservedBytes, record.ReservedBytes);
                }
                else recovered.Add(record.ResourceId, record);
            }
            sources.Add(ledger);
        }

        foreach (HostTempResourceRecord record in recovered.Values)
        {
            if (TryDelete(record)) continue;
            long retainedBytes = Math.Max(record.ReservedBytes, MeasureExistingBytes(record.Path));
            if (retainedBytes > 0 && !_budget.TryReserve(record.PluginId, record.PluginLimitBytes, retainedBytes))
                throw new InvalidOperationException("Abandoned host temp resources exceed the configured disk quota.");
            record.ReservedBytes = retainedBytes;
            record.CleanupPending = true;
            _records[record.ResourceId] = record;
        }
        // Publish ownership before retiring sources. A crash between these steps leaves deduplicable copies.
        Save();
        foreach (string ledger in sources)
        {
            try { File.Delete(ledger); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private HostTempResourceRecord Get(string resourceId) =>
        _records.TryGetValue(resourceId, out HostTempResourceRecord? record)
            ? record
            : throw new InvalidOperationException("Host temp resource is not registered.");

    private void Validate(HostTempResourceRecord record)
    {
        string full = Path.GetFullPath(record.Path);
        ArgumentException.ThrowIfNullOrEmpty(record.ResourceId);
        ArgumentException.ThrowIfNullOrEmpty(record.PluginId);
        ArgumentOutOfRangeException.ThrowIfNegative(record.ReservedBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(record.PluginLimitBytes);
        bool managedRoot = full.StartsWith(_hostTempRoot, StringComparison.OrdinalIgnoreCase);
        bool publicationLeaf = record.Kind == HostTempResourceKind.PublicationFile
            && Path.GetFileName(full).StartsWith(".ducom-", StringComparison.OrdinalIgnoreCase)
            && Path.GetExtension(full).Equals(".part", StringComparison.OrdinalIgnoreCase);
        if (!managedRoot && !publicationLeaf)
            throw new InvalidOperationException("Host temp ledger path is not a managed resource.");
    }

    private static bool TryDelete(HostTempResourceRecord record)
    {
        try
        {
            if (record.Kind == HostTempResourceKind.SnapshotDirectory)
            {
                if (Directory.Exists(record.Path)) Directory.Delete(record.Path, recursive: true);
                return !Directory.Exists(record.Path);
            }
            if (File.Exists(record.Path)) File.Delete(record.Path);
            return !File.Exists(record.Path);
        }
        catch (Exception exception)
        {
            PluginHostTrace.Warning($"Host temp resource could not be deleted: {record.ResourceId} at '{record.Path}'.", exception);
            return false;
        }
    }

    private static bool ResourceExists(HostTempResourceRecord record) =>
        record.Kind == HostTempResourceKind.SnapshotDirectory ? Directory.Exists(record.Path) : File.Exists(record.Path);

    private static long MeasureExistingBytes(string path)
    {
        try
        {
            if (File.Exists(path)) return new FileInfo(path).Length;
            if (Directory.Exists(path)) return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length);
        }
        catch (Exception exception)
        {
            // Reporting 0 under-counts the disk budget; keep the caller working but record why.
            PluginHostTrace.Warning($"Host temp usage measurement failed for '{path}'; reporting 0 bytes.", exception);
        }
        return 0;
    }

    private static HostTempResourceRecord Clone(HostTempResourceRecord record) => record with { };

    private void Save() => _writeLedger(_ledgerPath, JsonSerializer.Serialize(_records.Values.OrderBy(record => record.ResourceId)));

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _retryTimer.Dispose();
            try { Save(); }
            finally
            {
                foreach (FileStream abandoned in _abandonedLocks) abandoned.Dispose();
                _liveLock.Dispose();
            }
        }
    }
}
