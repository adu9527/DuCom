namespace DuCom.PluginHost.Core;

public sealed record SerialLeaseSnapshot(string LeaseId, string PluginId, string ActivationId, string TaskId, string Port, string? DeviceIdentity, bool RestoreSession, bool SessionWasOpen);

public sealed class SerialLeaseCoordinator
{
    private readonly object _gate = new();
    private readonly Dictionary<string, SerialLeaseSnapshot> _byPort = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SerialLeaseSnapshot> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SemaphoreSlim> _operationGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly AsyncLocal<string?> _ownerLease = new();

    public bool CanUse(string port)
    {
        lock (_gate) return !_byPort.TryGetValue(Normalize(port), out SerialLeaseSnapshot? lease) || lease.LeaseId == _ownerLease.Value;
    }

    public SerialLeaseSnapshot Acquire(string pluginId, string activationId, string taskId, string port, string? identity, bool restore, bool wasOpen)
    {
        string normalized = Normalize(port);
        lock (_gate)
        {
            if (_byPort.ContainsKey(normalized)) throw new InvalidOperationException($"Serial port '{normalized}' is already leased.");
            SerialLeaseSnapshot lease = new(Guid.NewGuid().ToString("N"), pluginId, activationId, taskId, normalized, identity, restore, wasOpen);
            _byPort.Add(normalized, lease);
            _byId.Add(lease.LeaseId, lease);
            return lease;
        }
    }

    public SerialLeaseSnapshot GetOwned(string pluginId, string activationId, string leaseId)
    {
        lock (_gate)
        {
            if (!_byId.TryGetValue(leaseId, out SerialLeaseSnapshot? lease) || lease.PluginId != pluginId || lease.ActivationId != activationId)
                throw new InvalidOperationException("Serial lease is missing or owned by another activation.");
            return lease;
        }
    }

    public void Release(string leaseId)
    {
        lock (_gate) if (_byId.Remove(leaseId, out SerialLeaseSnapshot? lease)) _byPort.Remove(lease.Port);
    }

    public void Revoke(string pluginId, string activationId)
    {
        lock (_gate)
        {
            string[] ids = [.. _byId.Values.Where(lease => lease.PluginId == pluginId && lease.ActivationId == activationId).Select(lease => lease.LeaseId)];
            foreach (string id in ids) if (_byId.Remove(id, out SerialLeaseSnapshot? lease)) _byPort.Remove(lease.Port);
        }
    }

    public async Task RunAsOwnerAsync(string leaseId, Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        string? previous = _ownerLease.Value;
        _ownerLease.Value = leaseId;
        try { await action().ConfigureAwait(false); }
        finally { _ownerLease.Value = previous; }
    }

    public async Task<T> RunPortOperationAsync<T>(string port, Func<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        SemaphoreSlim gate = GetOperationGate(port);
        await gate.WaitAsync().ConfigureAwait(false);
        try { return action(); }
        finally { gate.Release(); }
    }

    public async Task<T> RunPortOperationAsync<T>(string port, Func<Task<T>> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        SemaphoreSlim gate = GetOperationGate(port);
        await gate.WaitAsync().ConfigureAwait(false);
        try { return await action().ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    private SemaphoreSlim GetOperationGate(string port)
    {
        string normalized = Normalize(port);
        lock (_gate) return _operationGates.TryGetValue(normalized, out SemaphoreSlim? gate) ? gate : _operationGates[normalized] = new SemaphoreSlim(1, 1);
    }

    private static string Normalize(string port)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(port);
        return port.Trim().ToUpperInvariant();
    }
}
