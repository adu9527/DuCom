using System.Text.Json;
using DuCom.Plugin;
using DuCom.Plugin.Dto;
using DuCom.PluginHost.Diagnostics;

namespace DuCom.PluginHost.Core;

public sealed partial class PluginBroker
{
    private object LogsList()
    {
        RequirePermission(Permission.SerialLogsRead);
        return new SerialListResult
        {
            Sessions = [.. _environment.GetSerialSessions().Select(session => new SerialSessionInfo
            {
                SessionId = session.SessionId,
                Port = session.Port,
                Open = session.Open,
            })],
        };
    }

    private async Task<JsonElement?> SnapshotLogsAsync(LogsSnapshotRequest request, CancellationToken cancellationToken)
    {
        RequirePermission(Permission.SerialLogsRead);
        IReadOnlyList<HostLogSnapshot> snapshots = await _environment.CreateLogSnapshotsAsync(request.SessionId, cancellationToken).ConfigureAwait(false);
        _scope.ThrowIfRevoked();
        long reservation;
        try { reservation = snapshots.SelectMany(snapshot => snapshot.Files).Sum(file => checked(file.Length)); }
        catch (OverflowException) { throw new PluginScopeException(PluginErrorCode.ResourceLimit, "Log snapshot size overflowed the quota."); }
        if (reservation < 0 || reservation > _scope.Limits.TempQuotaBytes)
            throw new PluginScopeException(PluginErrorCode.ResourceLimit, "Log snapshot exceeds the plugin staging quota.");
        List<LogSnapshotFile> files = [];
        List<(string Path, long Length, string Port, string DisplayName)> grantFiles = [];
        long copiedBytes = 0;
        int index = 0;
        string snapshotRoot = Path.Combine(_scope.HostSnapshotDirectory, Guid.NewGuid().ToString("N"));
        string resourceId = _scope.RegisterResourceIntent(HostTempResourceKind.SnapshotDirectory, snapshotRoot);
        if (!_scope.TryReserveResource(resourceId, reservation))
        {
            _scope.CleanupResource(resourceId, HostTempResourceKind.SnapshotDirectory, snapshotRoot, 0);
            throw new PluginScopeException(PluginErrorCode.ResourceLimit, "Log snapshot exceeds the host or plugin staging quota.");
        }
        try
        {
            Directory.CreateDirectory(snapshotRoot);
            foreach (HostLogSnapshot snapshot in snapshots)
            {
                foreach (HostLogSnapshotFile file in snapshot.Files)
                {
                    if (file.Length < 0 || file.Length > _scope.Limits.TempQuotaBytes - copiedBytes)
                        throw new PluginScopeException(PluginErrorCode.ResourceLimit, "Log snapshot exceeds the activation temp quota.");
                    string snapshotPath = Path.Combine(snapshotRoot, index.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".log");
                    _fileIoCheckpoint?.Invoke("snapshot.before-copy", snapshotPath);
                    await CopySnapshotFileAsync(file.Path, snapshotPath, file.Length, cancellationToken).ConfigureAwait(false);
                    _fileIoCheckpoint?.Invoke("snapshot.after-copy", snapshotPath);
                    _scope.ThrowIfRevoked();
                    files.Add(new LogSnapshotFile { Index = index, Name = file.DisplayName, Length = file.Length, Port = file.Port });
                    grantFiles.Add((snapshotPath, file.Length, file.Port, file.DisplayName));
                    copiedBytes += file.Length;
                    index++;
                }
            }
            string token = _scope.CreateSnapshotGrant(resourceId, snapshotRoot, grantFiles, reservation);
            return Json(new LogsSnapshotResult { Token = token, Files = files, SessionCount = snapshots.Count });
        }
        catch
        {
            _scope.CleanupResource(resourceId, HostTempResourceKind.SnapshotDirectory, snapshotRoot, reservation);
            throw;
        }
    }

    private async Task<FilesReadResult> ReadLogChunkAsync(LogsReadRequest request)
    {
        RequirePermission(Permission.SerialLogsRead);
        SnapshotGrant grant = _scope.ResolveSnapshot(request.Token)
            ?? throw new PluginScopeException(PluginErrorCode.SessionExpired, "The log snapshot is expired or released.");
        if (request.Index < 0 || request.Index >= grant.Files.Count)
        {
            throw new PluginScopeException(PluginErrorCode.InvalidArgument, "Snapshot file index is out of range.");
        }

        (string path, long snapshotLength, string port, string displayName) = grant.Files[request.Index];
        if (!File.Exists(path))
        {
            throw new PluginScopeException(PluginErrorCode.SessionExpired, "The snapshotted file was deleted or rotated away.");
        }

        long boundary = new FileInfo(path).Length;
        if (boundary != snapshotLength)
        {
            throw new PluginScopeException(PluginErrorCode.InternalError, "The immutable log snapshot is incomplete.");
        }
        if (request.Offset < 0 || request.Offset >= boundary)
        {
            return new FilesReadResult { B64 = string.Empty, Length = 0 };
        }

        int length = Math.Clamp(request.Length, 1, _scope.Limits.ReadChunkMaxBytes);
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        stream.Seek(request.Offset, SeekOrigin.Begin);
        int toRead = (int)Math.Min(length, boundary - request.Offset);
        byte[] buffer = new byte[toRead];
        int read = 0;
        while (read < toRead)
        {
            int step = await stream.ReadAsync(buffer.AsMemory(read)).ConfigureAwait(false);
            if (step == 0)
            {
                break;
            }

            read += step;
        }

        return new FilesReadResult { B64 = Convert.ToBase64String(buffer.AsSpan(0, read)), Length = read };
    }

    private object ReleaseSnapshot(FilesTokenRequest request)
    {
        RequirePermission(Permission.SerialLogsRead);
        SnapshotGrant? grant = _scope.RemoveSnapshot(request.Token);
        if (grant is not null)
        {
            if (!_scope.TryReleaseSnapshotFiles(grant))
                _diagnostics.Write(PluginLogLevel.Warning, $"Snapshot cleanup failed for token {request.Token}; disk reservation retained.");
        }
        return new { ok = true };
    }

    private static async Task CopySnapshotFileAsync(string source, string destination, long expectedLength, CancellationToken cancellationToken)
    {
        if (!File.Exists(source))
            throw new PluginScopeException(PluginErrorCode.NotFound, "The log file no longer exists.");
        await using FileStream input = new(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using FileStream output = new(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] buffer = new byte[64 * 1024];
        long remaining = expectedLength;
        while (remaining > 0)
        {
            int read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                throw new PluginScopeException(PluginErrorCode.InternalError, "The log file ended before the snapshot boundary.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            remaining -= read;
        }
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
