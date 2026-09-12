using System.Security.Cryptography;
using DuCom.Plugin;
using DuCom.PluginHost.Diagnostics;

namespace DuCom.PluginHost.Core;

public sealed class FileGrant
{
    public required string Token { get; init; }
    public required string Path { get; init; }
    public required bool IsDirectory { get; init; }
    public required bool Write { get; init; }
    public required string TempPath { get; init; }
    public required DateTimeOffset ExpiresAtUtc { get; init; }
    public bool ReplacePathApproved { get; init; }
    public bool Committed { get; internal set; }
}

public sealed class SnapshotGrant
{
    public required string Token { get; init; }
    public required IReadOnlyList<(string Path, long Length, string Port, string DisplayName)> Files { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public required DateTimeOffset ExpiresAtUtc { get; init; }
    public required long ReservedBytes { get; init; }
    public required string ResourceId { get; init; }
    public required string RootPath { get; init; }
}

internal enum OutputState { Writable, Sealed, Preparing, Committing, Committed, Aborted }

internal sealed class OutputGrant
{
    public required string ResourceId { get; init; }
    public required string Path { get; init; }
    public required FileStream Stream { get; init; }
    public long Length { get; set; }
    public OutputState State { get; set; }
    public long ReservedBytes { get; set; }
}

/// <summary>
/// Per-activation authority: the granted permission set, scoped resource tokens, quotas, and
/// the revocation flag that every broker operation checks before doing anything. Revocation
/// happens at the first step of deactivation, not when the worker finally exits.
/// </summary>
public sealed class ActivationScope : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, FileGrant> _fileTokens = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SnapshotGrant> _snapshots = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OutputGrant> _outputs = new(StringComparer.Ordinal);
    private readonly HashSet<string> _permissions;
    private readonly HashSet<string> _capabilities;
    private long _storageBytes;
    private volatile bool _revoked;
    private readonly HostTempDiskBudget _hostTempDiskBudget;
    private readonly HostTempResourceLedger? _hostTempLedger;
    private readonly Timer _expiryTimer;

    public ActivationScope(PluginManifest manifest, string activationId, string storageDirectory, string workerScratchDirectory, string hostOutputDirectory, string hostSnapshotDirectory, PluginLimits limits, IEnumerable<string> grantedPermissions, HostTempDiskBudget? hostTempDiskBudget = null, HostTempResourceLedger? hostTempLedger = null)
    {
        Manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        ActivationId = activationId;
        StorageDirectory = storageDirectory;
        TempDirectory = workerScratchDirectory;
        HostOutputDirectory = hostOutputDirectory;
        HostSnapshotDirectory = hostSnapshotDirectory;
        Limits = limits;
        _permissions = new HashSet<string>(grantedPermissions, StringComparer.Ordinal);
        _capabilities = new HashSet<string>(manifest.Capabilities, StringComparer.Ordinal);
        _hostTempDiskBudget = hostTempDiskBudget ?? new HostTempDiskBudget(long.MaxValue);
        _hostTempLedger = hostTempLedger;
        _expiryTimer = new Timer(_ => ReapExpiredSnapshots(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    public PluginManifest Manifest { get; }

    public string ActivationId { get; }

    public string StorageDirectory { get; }

    public string TempDirectory { get; }

    public string HostOutputDirectory { get; }

    public string HostSnapshotDirectory { get; }

    public PluginLimits Limits { get; }

    public bool Revoked => _revoked;

    public IReadOnlyCollection<string> GrantedPermissions => _permissions;

    public bool HasPermission(string permission) => _permissions.Contains(permission);

    public bool HasCapability(string capability) => _capabilities.Contains(capability);

    public void Revoke()
    {
        SnapshotGrant[] snapshots;
        OutputGrant[] outputs;
        lock (_gate)
        {
            if (_revoked) return;
            _revoked = true;
            _expiryTimer.Dispose();
            _fileTokens.Clear();
            snapshots = [.. _snapshots.Values];
            _snapshots.Clear();
            outputs = [.. _outputs.Values];
            _outputs.Clear();
        }
        foreach (SnapshotGrant snapshot in snapshots) TryReleaseSnapshotFiles(snapshot);
        foreach (OutputGrant output in outputs)
        {
            try { output.Stream.Dispose(); }
            catch (Exception exception)
            {
                PluginHostTrace.Warning($"Output stream disposal failed during revocation of '{output.ResourceId}' (activation {ActivationId}).", exception);
            }

            CleanupOutput(output);
        }
    }

    // The filesystem publication and token consumption share revocation's linearization gate.
    internal T WithAuthority<T>(Func<T> action)
    {
        lock (_gate)
        {
            ThrowIfRevoked();
            return action();
        }
    }

    internal string RegisterOutput(string resourceId, string path, FileStream stream) => WithAuthority(() =>
    {
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        _outputs.Add(token, new OutputGrant { ResourceId = resourceId, Path = path, Stream = stream });
        return token;
    });

    internal OutputGrant ResolveOutput(string token) => WithAuthority(() =>
        !string.IsNullOrEmpty(token) && _outputs.TryGetValue(token, out OutputGrant? output)
            ? output
            : throw new PluginScopeException(PluginErrorCode.SessionExpired, "The output token is invalid or expired."));

    internal void MarkOutputAfterCommitFailure(OutputGrant output)
    {
        lock (_gate)
        {
            if (output.State is OutputState.Preparing or OutputState.Committing)
                output.State = OutputState.Sealed;
        }
    }

    internal void RemoveOutput(string token)
    {
        lock (_gate)
        {
            if (_outputs.Remove(token, out OutputGrant? output))
            {
                output.Stream.Dispose();
            }
        }
    }

    internal bool TryReserveOutput(OutputGrant output, long bytes)
    {
        if (!TryReserveResource(output.ResourceId, bytes)) return false;
        output.ReservedBytes += bytes;
        return true;
    }

    internal void ReleaseOutputReservation(OutputGrant output)
    {
        if (output.ReservedBytes == 0) return;
        ReleaseResourceReservation(output.ResourceId, output.ReservedBytes);
        output.ReservedBytes = 0;
    }

    internal bool CleanupOutput(OutputGrant output)
    {
        lock (_gate)
        {
            if (_hostTempLedger is not null)
            {
                try { return _hostTempLedger.TryCleanup(output.ResourceId); }
                catch { return false; } // Keep the resource and reservation available for retry.
            }
            if (!TryDelete(output.Path)) return false;
            if (output.ReservedBytes > 0) _hostTempDiskBudget.Release(Manifest.Id, output.ReservedBytes);
            output.ReservedBytes = 0;
            return true;
        }
    }

    internal string RegisterResourceIntent(HostTempResourceKind kind, string path)
    {
        string resourceId = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        _hostTempLedger?.RegisterIntent(new HostTempResourceRecord
        {
            ResourceId = resourceId,
            PluginId = Manifest.Id,
            ActivationId = ActivationId,
            Kind = kind,
            Path = path,
            PluginLimitBytes = Limits.TempQuotaBytes,
        });
        return resourceId;
    }

    internal bool TryReserveResource(string resourceId, long bytes) =>
        _hostTempLedger?.TryReserve(resourceId, bytes)
        ?? _hostTempDiskBudget.TryReserve(Manifest.Id, Limits.TempQuotaBytes, bytes);

    internal void ReleaseResourceReservation(string resourceId, long bytes)
    {
        if (_hostTempLedger is not null) _hostTempLedger.ReleaseReservation(resourceId, bytes);
        else _hostTempDiskBudget.Release(Manifest.Id, bytes);
    }

    internal bool CleanupResource(string resourceId, HostTempResourceKind kind, string path, long reservedBytes)
    {
        if (_hostTempLedger is not null)
        {
            try { return _hostTempLedger.TryCleanup(resourceId); }
            catch { return false; }
        }
        bool deleted = kind == HostTempResourceKind.SnapshotDirectory ? TryDeleteDirectory(path) : TryDelete(path);
        if (deleted && reservedBytes > 0) _hostTempDiskBudget.Release(Manifest.Id, reservedBytes);
        return deleted;
    }

    public void ThrowIfRevoked()
    {
        if (_revoked)
        {
            throw new PluginScopeException(PluginErrorCode.SessionExpired, "The activation has been deactivated.");
        }
    }

    public string CreateFileGrant(string path, bool isDirectory, bool write, bool replacePathApproved = false)
    {
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        lock (_gate)
        {
            ThrowIfRevoked();
            _fileTokens[token] = new FileGrant
            {
                Token = token,
                Path = path,
                IsDirectory = isDirectory,
                Write = write,
                TempPath = write ? Path.Combine(TempDirectory, $"pick-{token}.tmp") : string.Empty,
                ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(write ? 24 : 8),
                ReplacePathApproved = replacePathApproved,
            };
        }

        return token;
    }

    public FileGrant? ResolveToken(string token, bool write)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        lock (_gate)
        {
            if (!_fileTokens.TryGetValue(token, out FileGrant? grant))
            {
                return null;
            }

            if (grant.Write != write || grant.ExpiresAtUtc < DateTimeOffset.UtcNow)
            {
                return null;
            }

            return grant;
        }
    }

    public void RemoveToken(string token)
    {
        lock (_gate)
        {
            _fileTokens.Remove(token);
        }
    }

    public string CreateSnapshotGrant(string resourceId, string rootPath, IReadOnlyList<(string Path, long Length, string Port, string DisplayName)> files, long reservedBytes)
    {
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        lock (_gate)
        {
            ThrowIfRevoked();
            DateTimeOffset now = DateTimeOffset.UtcNow;
            _snapshots[token] = new SnapshotGrant
            {
                Token = token,
                Files = files,
                CreatedAtUtc = now,
                ExpiresAtUtc = now.AddMinutes(15),
                ReservedBytes = reservedBytes,
                ResourceId = resourceId,
                RootPath = rootPath,
            };
        }

        return token;
    }

    public SnapshotGrant? ResolveSnapshot(string token)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        lock (_gate)
        {
            if (!_snapshots.TryGetValue(token, out SnapshotGrant? grant))
            {
                return null;
            }

            if (grant.ExpiresAtUtc < DateTimeOffset.UtcNow)
            {
                _snapshots.Remove(token);
                _ = Task.Run(() => TryReleaseSnapshotFiles(grant));
                return null;
            }

            return grant;
        }
    }

    public SnapshotGrant? RemoveSnapshot(string token)
    {
        lock (_gate)
        {
            _snapshots.Remove(token, out SnapshotGrant? grant);
            return grant;
        }
    }

    internal bool TryReleaseSnapshotFiles(SnapshotGrant grant)
    {
        return CleanupResource(grant.ResourceId, HostTempResourceKind.SnapshotDirectory, grant.RootPath, grant.ReservedBytes);
    }

    private void ReapExpiredSnapshots()
    {
        SnapshotGrant[] expired;
        lock (_gate)
        {
            if (_revoked) return;
            DateTimeOffset now = DateTimeOffset.UtcNow;
            expired = [.. _snapshots.Values.Where(grant => grant.ExpiresAtUtc <= now)];
            foreach (SnapshotGrant grant in expired) _snapshots.Remove(grant.Token);
        }
        foreach (SnapshotGrant grant in expired) TryReleaseSnapshotFiles(grant);
        _hostTempLedger?.RetryPendingCleanups();
    }

    private static bool TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            return !Directory.Exists(path);
        }
        catch (Exception) { return false; }
    }

    private static bool TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); return !File.Exists(path); } catch (Exception) { return false; }
    }

    public long StorageBytes => Volatile.Read(ref _storageBytes);

    public void AddStorageBytes(long delta) => Interlocked.Add(ref _storageBytes, delta);

    public long MeasureTempBytes()
    {
        try
        {
            return Directory.Exists(TempDirectory)
                ? Directory.EnumerateFiles(TempDirectory, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length)
                : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    public void Dispose() => Revoke();
}

public sealed class PluginScopeException : Exception
{
    public PluginScopeException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}
