using System.Text.Json;
using DuCom.Plugin;
using DuCom.Plugin.Dto;
using DuCom.PluginHost.Diagnostics;

namespace DuCom.PluginHost.Core;

public sealed partial class PluginBroker
{
    private OutputBeginResult BeginOutput()
    {
        RequirePermission(Permission.FilesUserSelectedWrite);
        return _scope.WithAuthority(() =>
        {
            string outputRoot = _scope.HostOutputDirectory;
            RejectReparsePoints(outputRoot);
            Directory.CreateDirectory(outputRoot);
            string path = Path.Combine(outputRoot, Guid.NewGuid().ToString("N") + ".part");
            string resourceId = _scope.RegisterResourceIntent(HostTempResourceKind.OutputFile, path);
            FileStream? stream = null;
            try
            {
                stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                string token = _scope.RegisterOutput(resourceId, path, stream);
                return new OutputBeginResult { Token = token, MaxBytes = _scope.Limits.TempQuotaBytes, ChunkMaxBytes = 384 * 1024 };
            }
            catch
            {
                stream?.Dispose();
                _scope.CleanupResource(resourceId, HostTempResourceKind.OutputFile, path, 0);
                throw;
            }
        });
    }

    private Task<OutputWriteResult> WriteOutputAsync(OutputWriteRequest request, CancellationToken cancellationToken)
    {
        RequirePermission(Permission.FilesUserSelectedWrite);
        byte[] data;
        try { data = Convert.FromBase64String(request.B64); }
        catch (FormatException) { throw new PluginScopeException(PluginErrorCode.InvalidArgument, "Output data is not base64."); }
        if (data.Length == 0 || data.Length > 384 * 1024 || request.Offset < 0)
            throw new PluginScopeException(PluginErrorCode.InvalidArgument, "Output chunk is out of bounds.");

        cancellationToken.ThrowIfCancellationRequested();
        OutputWriteResult result = _scope.WithAuthority(() =>
        {
            OutputGrant output = _scope.ResolveOutput(request.Token);
            if (output.State != OutputState.Writable || request.Offset != output.Length)
                throw new PluginScopeException(PluginErrorCode.InvalidArgument, "Output chunks must be strictly ordered and non-repeating.");
            if (data.Length > _scope.Limits.TempQuotaBytes - output.Length)
                throw new PluginScopeException(PluginErrorCode.ResourceLimit, "Output exceeds the activation quota.");
            if (!_scope.TryReserveOutput(output, data.Length))
                throw new PluginScopeException(PluginErrorCode.ResourceLimit, "Host output staging quota is exhausted.");
            try
            {
                long oldLength = output.Length;
                output.Stream.Write(data, 0, data.Length);
                output.Length = oldLength + data.Length;
            }
            catch
            {
                long actualLength = SafeLength(output.Path);
                long retained = Math.Clamp(actualLength - output.Length, 0, data.Length);
                try
                {
                    output.Stream.SetLength(output.Length);
                    output.Stream.Position = output.Length;
                    retained = 0;
                }
                catch
                {
                    output.State = OutputState.Aborted;
                }
                long release = data.Length - retained;
                if (release > 0)
                {
                    _scope.ReleaseResourceReservation(output.ResourceId, release);
                    output.ReservedBytes -= release;
                }
                throw;
            }
            return new OutputWriteResult { Length = output.Length };
        });
        return Task.FromResult(result);
    }

    private async Task<JsonElement?> CommitOutputAsync(OutputCommitRequest request, CancellationToken cancellationToken)
    {
        RequirePermission(Permission.FilesUserSelectedWrite);
        ArgumentException.ThrowIfNullOrEmpty(request.CommitId);
        lock (_commitGate)
        {
            if (_commitResults.TryGetValue(request.CommitId, out OutputCommitStatusResult? existing))
            {
                if (existing.State == "committed") return Json(new FilesCommitResult { FinalPath = existing.FinalPath!, Bytes = existing.Bytes });
                throw new PluginScopeException(existing.ErrorCode ?? PluginErrorCode.InvalidArgument, "This commit id already reached a terminal state.");
            }
            _commitResults[request.CommitId] = new OutputCommitStatusResult { State = "preparing" };
        }
        OutputGrant? pendingOutput = null;
        try
        {
            (OutputGrant Output, FileGrant Target) preparing = _scope.WithAuthority(() =>
            {
                OutputGrant output = _scope.ResolveOutput(request.Token);
                FileGrant target = ResolveGrant(request.TargetToken, write: true);
                if (output.State is not (OutputState.Writable or OutputState.Sealed))
                    throw new PluginScopeException(PluginErrorCode.InvalidArgument, "Output is not writable or is already committing.");
                output.State = OutputState.Preparing;
                pendingOutput = output;
                return (output, target);
            });

            if (preparing.Output.Stream.CanWrite)
            {
                await Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    preparing.Output.Stream.Flush(flushToDisk: true);
                    preparing.Output.Stream.Dispose();
                }, CancellationToken.None).ConfigureAwait(false);
            }
            FilesCommitResult result = await PublishFileAsync(preparing.Output, preparing.Target, request.CommitId, cancellationToken).ConfigureAwait(false);
            if (!_scope.CleanupOutput(preparing.Output))
                _diagnostics.Write(PluginLogLevel.Warning, $"Committed output cleanup is pending for token {request.Token}; disk reservation retained.");
            else _scope.RemoveOutput(request.Token);
            return Json(result);
        }
        catch (Exception exception)
        {
            if (pendingOutput is not null) _scope.MarkOutputAfterCommitFailure(pendingOutput);
            OutputCommitStatusResult current = GetCommitStatus(new OutputCommitStatusRequest { CommitId = request.CommitId });
            if (current.State == "committed")
            {
                _diagnostics.Write(PluginLogLevel.Warning, $"Post-commit cleanup failed for {request.CommitId}: {exception.Message}");
                return Json(new FilesCommitResult { FinalPath = current.FinalPath!, Bytes = current.Bytes });
            }
            if (current.State != "committed")
                RecordCommit(request.CommitId, new OutputCommitStatusResult
                {
                    State = "aborted",
                    ErrorCode = cancellationToken.IsCancellationRequested
                        ? PluginErrorCode.Cancelled
                        : exception is PluginScopeException scopeException ? scopeException.Code : PluginErrorCode.InternalError,
                });
            throw;
        }
    }

    private OutputCommitStatusResult GetCommitStatus(OutputCommitStatusRequest request)
    {
        ArgumentException.ThrowIfNullOrEmpty(request.CommitId);
        lock (_commitGate)
            return _commitResults.GetValueOrDefault(request.CommitId) ?? new OutputCommitStatusResult { State = "unknown" };
    }

    private void RecordCommit(string commitId, OutputCommitStatusResult result)
    {
        lock (_commitGate)
        {
            if (_commitResults.Count >= 256 && !_commitResults.ContainsKey(commitId))
                _commitResults.Remove(_commitResults.Keys.First());
            _commitResults[commitId] = result;
        }
    }

    private object DiscardOutput(FilesTokenRequest request)
    {
        RequirePermission(Permission.FilesUserSelectedWrite);
        return _scope.WithAuthority<object>(() =>
        {
            OutputGrant output = _scope.ResolveOutput(request.Token);
            if (output.State is OutputState.Preparing or OutputState.Committing)
                throw new PluginScopeException(PluginErrorCode.InvalidArgument, "Output commit is in progress.");
            if (output.State != OutputState.Committed) output.State = OutputState.Aborted;
            output.Stream.Dispose();
            bool deleted = _scope.CleanupOutput(output);
            if (!deleted)
            {
                _diagnostics.Write(PluginLogLevel.Warning, $"Output cleanup failed for token {request.Token}; disk reservation retained.");
                throw new PluginScopeException(PluginErrorCode.InternalError, "Output cleanup is pending; retry discard.");
            }
            _scope.RemoveOutput(request.Token);
            return new { ok = true };
        });
    }

    private async Task<FilesCommitResult> PublishFileAsync(OutputGrant output, FileGrant target, string commitId, CancellationToken cancellationToken)
    {
        if (target.Committed || target.IsDirectory)
            throw new PluginScopeException(PluginErrorCode.SessionExpired, "The write token is not available.");
        string source = output.Path;
        RejectReparsePoints(source);
        if (!File.Exists(source))
            throw new PluginScopeException(PluginErrorCode.NotFound, "Nothing was written for this token.");
        long bytes = new FileInfo(source).Length;
        if (bytes > _scope.Limits.TempQuotaBytes)
            throw new PluginScopeException(PluginErrorCode.ResourceLimit, "Temp output exceeds the quota.");
        RejectReparsePoints(target.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(target.Path)!);
        using DirectoryLease sourceLease = new(Path.GetDirectoryName(source)!);
        using DirectoryLease targetLease = new(Path.GetDirectoryName(target.Path)!);
        RejectReparsePoints(source);
        RejectReparsePoints(target.Path);
        // Stage beside the user-selected target before replacement: File.Replace cannot cross
        // volumes, while the host output root commonly lives on a different disk.
        string targetStaging = Path.Combine(Path.GetDirectoryName(target.Path)!, $".ducom-{Guid.NewGuid():N}.part");
        string publicationResourceId = _scope.RegisterResourceIntent(HostTempResourceKind.PublicationFile, targetStaging);
        if (!_scope.TryReserveResource(publicationResourceId, bytes))
        {
            _scope.CleanupResource(publicationResourceId, HostTempResourceKind.PublicationFile, targetStaging, 0);
            throw new PluginScopeException(PluginErrorCode.ResourceLimit, "Target-side publication staging quota is exhausted.");
        }
        try
        {
            await using FileStream input = new(source, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using FileStream staged = CreateSecureStaging(targetStaging);
            _fileIoCheckpoint?.Invoke("publish.staging-open", targetStaging);
            await input.CopyToAsync(staged, 64 * 1024, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            staged.Flush(flushToDisk: true);
            _fileIoCheckpoint?.Invoke("publish.before-commit", targetStaging);
            _scope.WithAuthority(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (target.Committed || !ReferenceEquals(_scope.ResolveToken(target.Token, write: true), target))
                    throw new PluginScopeException(PluginErrorCode.SessionExpired, "The write token is not available.");
                output.State = OutputState.Committing;
                RenameByHandle(staged.SafeFileHandle, target.Path, target.ReplacePathApproved);
                // No cleanup or other fallible I/O may precede the irreversible success record.
                output.State = OutputState.Committed;
                target.Committed = true;
                _scope.RemoveToken(target.Token);
                RecordCommit(commitId, new OutputCommitStatusResult { State = "committed", FinalPath = target.Path, Bytes = bytes });
                return true;
            });
        }
        finally
        {
            try { _fileIoCheckpoint?.Invoke("publish.before-cleanup", targetStaging); }
            finally
            {
                if (!_scope.CleanupResource(publicationResourceId, HostTempResourceKind.PublicationFile, targetStaging, bytes))
                    _diagnostics.Write(PluginLogLevel.Warning, $"Publication staging cleanup is pending for resource {publicationResourceId}, '{Path.GetFileName(targetStaging)}'.");
            }
        }
        return new FilesCommitResult { FinalPath = target.Path, Bytes = bytes };
    }
}
