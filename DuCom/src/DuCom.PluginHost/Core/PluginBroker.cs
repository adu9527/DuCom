using System.Text.Json;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using DuCom.Core.Persistence;
using DuCom.Plugin;
using DuCom.Plugin.Dto;
using DuCom.PluginHost.Diagnostics;

namespace DuCom.PluginHost.Core;

/// <summary>
/// Executes broker operations requested by a worker against one activation scope: private
/// storage, host-mediated file access, log snapshots, transactional output, declarative UI
/// updates, background switching, and diagnostics. Every operation checks scope revocation
/// and the manifest permission before touching any resource.
/// </summary>
public sealed class PluginBroker
{
    private readonly ActivationScope _scope;
    private readonly IPluginHostEnvironment _environment;
    private readonly PluginDiagnosticsLog _diagnostics;
    private readonly string _storageFilePath;
    private readonly int _diagAllowancePerMinute;
    private readonly Queue<long> _diagWindow = new();
    private readonly Queue<long> _uiWindow = new();
    private readonly object _commitGate = new();
    private readonly Dictionary<string, OutputCommitStatusResult> _commitResults = new(StringComparer.Ordinal);

    internal static Action<string, string>? FileIoTestHook { get; set; }

    public PluginBroker(ActivationScope scope, IPluginHostEnvironment environment, PluginDiagnosticsLog diagnostics)
    {
        _scope = scope ?? throw new ArgumentNullException(nameof(scope));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _storageFilePath = Path.Combine(scope.StorageDirectory, "config.json");
        _diagAllowancePerMinute = Math.Max(scope.Limits.DiagMessagesPerMinute, 1);
    }

    public event Action<string, string>? SerialSubscriptionAdded;

    public event EventHandler<string>? SerialSubscriptionRemoved;

    public async Task<JsonElement?> ExecuteAsync(string operation, JsonElement? data, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(operation);
        _scope.ThrowIfRevoked();
        return operation switch
        {
            PluginOps.StorageRead => Json(StorageRead()),
            PluginOps.StorageWrite => Json(await StorageWriteAsync(Require<StorageWriteRequest>(data))),
            PluginOps.FilesPickRead => Json(await PickReadAsync(Require<FilesPickReadRequest>(data), cancellationToken)),
            PluginOps.FilesPickWrite => Json(await PickWriteAsync(Require<FilesPickWriteRequest>(data), cancellationToken)),
            PluginOps.FilesHostPaths => Json(HostPaths()),
            PluginOps.FilesCreateWriteTarget => Json(CreateWriteTarget(Require<FilesCreateWriteTargetRequest>(data))),
            PluginOps.FilesStat => Json(Stat(Require<FilesStatRequest>(data))),
            PluginOps.FilesRead => Json(await ReadChunkAsync(Require<FilesReadRequest>(data))),
            PluginOps.FilesList => Json(List(Require<FilesListRequest>(data))),
            PluginOps.FilesRemembered => Json(Remembered(Require<FilesRememberedRequest>(data))),
            PluginOps.SerialList => Json(SerialList()),
            PluginOps.SerialSubscribe => Json(SubscribeSerial(Require<SerialSubscribeRequest>(data))),
            PluginOps.SerialUnsubscribe => Json(UnsubscribeSerial(Require<FilesTokenRequest>(data))),
            PluginOps.LogsList => Json(LogsList()),
            PluginOps.LogsSnapshot => await SnapshotLogsAsync(Require<LogsSnapshotRequest>(data), cancellationToken),
            PluginOps.LogsRead => Json(await ReadLogChunkAsync(Require<LogsReadRequest>(data))),
            PluginOps.LogsRelease => Json(ReleaseSnapshot(Require<FilesTokenRequest>(data))),
            PluginOps.OutputBegin => Json(BeginOutput()),
            PluginOps.OutputWrite => Json(await WriteOutputAsync(Require<OutputWriteRequest>(data), cancellationToken)),
            PluginOps.OutputCommit => await CommitOutputAsync(Require<OutputCommitRequest>(data), cancellationToken),
            PluginOps.OutputCommitStatus => Json(GetCommitStatus(Require<OutputCommitStatusRequest>(data))),
            PluginOps.OutputDiscard => Json(DiscardOutput(Require<FilesTokenRequest>(data))),
            PluginOps.UiUpdate => Json(UpdateUi(Require<UiUpdateRequest>(data))),
            PluginOps.UiNotify => await NotifyUiAsync(Require<UiNotifyRequest>(data)),
            PluginOps.BackgroundSet => Json(BackgroundSet(Require<BackgroundSetRequest>(data))),
            PluginOps.BackgroundClear => Json(BackgroundClear()),
            _ => throw new PluginScopeException(PluginErrorCode.UnsupportedOperation, $"Unknown operation '{operation}'."),
        };
    }

    public void WriteDiagnostic(string level, string message)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        lock (_diagWindow)
        {
            while (_diagWindow.Count > 0 && _diagWindow.Peek() < now - 60_000)
            {
                _diagWindow.Dequeue();
            }

            if (_diagWindow.Count >= _diagAllowancePerMinute)
            {
                return;
            }

            _diagWindow.Enqueue(now);
        }

        PluginLogLevel parsed = level switch
        {
            "info" => PluginLogLevel.Info,
            "warning" => PluginLogLevel.Warning,
            "error" => PluginLogLevel.Error,
            _ => PluginLogLevel.Info,
        };
        _diagnostics.Write(parsed, message);
    }

    private static JsonElement? Json(object? value) =>
        value is null ? null : JsonSerializer.SerializeToElement(value, DtoJson.Options);

    private static T Require<T>(JsonElement? data) =>
        data is null
            ? throw new PluginScopeException(PluginErrorCode.InvalidArgument, "Request payload is missing.")
            : data.Value.Deserialize<T>(DtoJson.Options)
                ?? throw new PluginScopeException(PluginErrorCode.InvalidArgument, "Request payload is malformed.");

    private void RequirePermission(string permission)
    {
        if (!_scope.HasPermission(permission))
        {
            throw new PluginScopeException(PluginErrorCode.PermissionDenied, $"The manifest does not grant '{permission}'.");
        }
    }

    private StorageReadResult StorageRead()
    {
        RequirePermission(Permission.StorageOwn);
        if (!File.Exists(_storageFilePath))
        {
            return new StorageReadResult { Data = null, Bytes = 0 };
        }

        string json = File.ReadAllText(_storageFilePath);
        return new StorageReadResult { Data = json, Bytes = json.Length };
    }

    private async Task<object> StorageWriteAsync(StorageWriteRequest request)
    {
        RequirePermission(Permission.StorageOwn);
        if (request.Data.Length > _scope.Limits.StorageQuotaBytes)
        {
            throw new PluginScopeException(PluginErrorCode.ResourceLimit, $"Storage exceeds the {_scope.Limits.StorageQuotaBytes} byte quota.");
        }

        Directory.CreateDirectory(_scope.StorageDirectory);
        await Task.Run(() => AtomicFileStore.WriteAllText(_storageFilePath, request.Data), CancellationToken.None).ConfigureAwait(false);
        _scope.AddStorageBytes(request.Data.Length);
        return new { bytes = request.Data.Length };
    }

    private async Task<FilesPickResult?> PickReadAsync(FilesPickReadRequest request, CancellationToken cancellationToken)
    {
        RequirePermission(Permission.FilesUserSelectedRead);
        HostPickResult? picked = await _environment.PickReadAsync(_scope.Manifest.Id, new HostPickRequest
        {
            Mode = request.Mode,
            FilterName = request.FilterName,
            Extensions = request.Extensions,
            Remember = request.Remember,
        }, cancellationToken).ConfigureAwait(false);
        if (picked is null)
        {
            throw new PluginScopeException(PluginErrorCode.Cancelled, "The user cancelled the picker.");
        }

        string token = _scope.CreateFileGrant(picked.DisplayPath, picked.IsDirectory, write: false);
        return new FilesPickResult
        {
            Token = token,
            DisplayPath = picked.DisplayPath,
            IsDirectory = picked.IsDirectory,
            Entries = [.. picked.Entries.Select(entry => new PickedEntry
            {
                Name = entry.Name,
                IsDirectory = entry.IsDirectory,
                Length = entry.Length,
            })],
            Remembered = picked.Remembered,
        };
    }

    private async Task<FilesPickResult?> PickWriteAsync(FilesPickWriteRequest request, CancellationToken cancellationToken)
    {
        RequirePermission(Permission.FilesUserSelectedWrite);
        bool directory = string.Equals(request.Mode, "directory", StringComparison.Ordinal);
        HostPickResult? picked = await _environment.PickWriteAsync(_scope.Manifest.Id, new HostPickRequest
        {
            Mode = directory ? "directory" : "saveFile",
            FilterName = request.FilterName,
            Extensions = request.Extensions,
            SuggestName = request.SuggestName,
        }, cancellationToken).ConfigureAwait(false);
        if (picked is null)
        {
            throw new PluginScopeException(PluginErrorCode.Cancelled, "The user cancelled the picker.");
        }

        string token = _scope.CreateFileGrant(picked.DisplayPath, picked.IsDirectory, write: true, picked.ReplacePathApproved);
        return new FilesPickResult
        {
            Token = token,
            DisplayPath = picked.DisplayPath,
            IsDirectory = picked.IsDirectory,
            Entries = [],
            Remembered = picked.Remembered,
        };
    }

    private HostPathsResult HostPaths()
    {
        RequirePermission(Permission.FilesUserSelectedWrite);
        return new HostPathsResult { LogDirectory = _environment.GetLogDirectory() };
    }

    private FilesPickResult CreateWriteTarget(FilesCreateWriteTargetRequest request)
    {
        RequirePermission(Permission.FilesUserSelectedWrite);
        if (string.IsNullOrWhiteSpace(request.SuggestName))
        {
            throw new PluginScopeException(PluginErrorCode.InvalidArgument, "A suggested file name is required.");
        }

        string directory;
        if (string.IsNullOrWhiteSpace(request.Directory))
        {
            directory = _environment.GetLogDirectory();
        }
        else
        {
            string? resolved = _environment.TryResolveRememberedReadPath(_scope.Manifest.Id, request.Directory);
            if (resolved is null)
            {
                throw new PluginScopeException(PermissionDeniedCode, "This directory was never granted by the user.");
            }

            directory = resolved;
        }

        RejectReparsePoints(directory);
        Directory.CreateDirectory(directory);
        RejectReparsePoints(directory);
        string targetPath = GetAvailableOutputPath(directory, SanitizeTargetName(request.SuggestName));
        string token = _scope.CreateFileGrant(targetPath, isDirectory: false, write: true);
        return new FilesPickResult
        {
            Token = token,
            DisplayPath = targetPath,
            IsDirectory = false,
            Entries = [],
            Remembered = false,
        };
    }

    private static string SanitizeTargetName(string value)
    {
        char[] invalid = [.. Path.GetInvalidFileNameChars()];
        string sanitized = string.Join("_", value.Split(invalid, StringSplitOptions.RemoveEmptyEntries)).TrimEnd('.', ' ');
        if (sanitized.Length > 160)
        {
            sanitized = sanitized[..160].TrimEnd('.', ' ');
        }

        return sanitized.Length == 0 ? "DuCom日志包.zip" : sanitized;
    }

    private static string GetAvailableOutputPath(string directory, string fileName)
    {
        string stem = Path.GetFileNameWithoutExtension(fileName);
        string extension = Path.GetExtension(fileName);
        string path = Path.Combine(directory, fileName);
        for (int collision = 1; File.Exists(path); collision++)
        {
            if (collision > 99)
            {
                return Path.Combine(directory, $"{stem}-{Guid.NewGuid():N}{extension}");
            }

            path = Path.Combine(directory, $"{stem}-{collision:D2}{extension}");
        }

        return path;
    }

    private async Task<JsonElement?> NotifyUiAsync(UiNotifyRequest request)
    {
        if (!_scope.HasCapability(Capability.ToolPage))
        {
            throw new PluginScopeException(PluginErrorCode.PermissionDenied, "The manifest does not declare tool-page.");
        }

        if (string.IsNullOrWhiteSpace(request.Title) || request.Title.Length > 200
            || string.IsNullOrWhiteSpace(request.Message) || request.Message.Length > 4000
            || (request.RevealPath is { Length: > 0 } && request.RevealPath.Length > 1000))
        {
            throw new PluginScopeException(PluginErrorCode.InvalidArgument, "The notice title, message, or reveal path is invalid.");
        }

        bool shown = await _environment.ShowNoticeAsync(new HostPluginNotice
        {
            PluginId = _scope.Manifest.Id,
            Title = request.Title,
            Message = request.Message,
            RevealPath = string.IsNullOrEmpty(request.RevealPath) ? null : request.RevealPath,
        }).ConfigureAwait(false);
        if (!shown)
        {
            throw new PluginScopeException(PluginErrorCode.InternalError, "The notice could not be displayed.");
        }

        return Json(new { ok = true });
    }

    private FilesStatResult Stat(FilesStatRequest request)
    {
        RequirePermission(Permission.FilesUserSelectedRead);
        FileGrant grant = ResolveGrant(request.Token, write: false);
        if (grant.IsDirectory || !File.Exists(grant.Path))
        {
            return new FilesStatResult { Exists = false, Length = 0 };
        }

        return new FilesStatResult { Exists = true, Length = new FileInfo(grant.Path).Length };
    }

    private async Task<FilesReadResult> ReadChunkAsync(FilesReadRequest request)
    {
        RequirePermission(Permission.FilesUserSelectedRead);
        FileGrant grant = ResolveGrant(request.Token, write: false);
        if (grant.IsDirectory || !File.Exists(grant.Path))
        {
            throw new PluginScopeException(PluginErrorCode.NotFound, "The granted file no longer exists.");
        }

        int length = Math.Clamp(request.Length, 1, _scope.Limits.ReadChunkMaxBytes);
        using FileStream stream = new(grant.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (request.Offset < 0 || request.Offset > stream.Length)
        {
            throw new PluginScopeException(PluginErrorCode.InvalidArgument, "Read offset is out of range.");
        }

        stream.Seek(request.Offset, SeekOrigin.Begin);
        byte[] buffer = new byte[Math.Min(length, stream.Length - request.Offset)];
        int read = 0;
        while (read < buffer.Length)
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

    private FilesListResult List(FilesListRequest request)
    {
        RequirePermission(Permission.FilesUserSelectedRead);
        FileGrant grant = ResolveGrant(request.Token, write: false);
        string directory = grant.IsDirectory ? grant.Path : Path.GetDirectoryName(grant.Path)!;
        if (!Directory.Exists(directory))
        {
            throw new PluginScopeException(PluginErrorCode.NotFound, "The granted directory no longer exists.");
        }

        List<PickedEntry> entries = [];
        foreach (string entry in Directory.EnumerateFileSystemEntries(directory).Take(2000))
        {
            bool isDirectory = Directory.Exists(entry);
            entries.Add(new PickedEntry
            {
                Name = Path.GetFileName(entry),
                IsDirectory = isDirectory,
                Length = isDirectory ? 0 : SafeLength(entry),
            });
        }

        return new FilesListResult { Entries = entries };
    }

    private static long SafeLength(string file)
    {
        try
        {
            return new FileInfo(file).Length;
        }
        catch (Exception)
        {
            return 0;
        }
    }


    private object Remembered(FilesRememberedRequest request)
    {
        RequirePermission(Permission.FilesUserSelectedRead);
        ArgumentException.ThrowIfNullOrEmpty(request.Path);
        string? resolved = _environment.TryResolveRememberedReadPath(_scope.Manifest.Id, request.Path);
        if (resolved is null)
        {
            throw new PluginScopeException(PermissionDeniedCode, "This path was never granted by the user.");
        }

        bool existsAsFile = File.Exists(resolved);
        bool isDirectory = !existsAsFile && Directory.Exists(resolved);
        if (!existsAsFile && !isDirectory)
        {
            throw new PluginScopeException(PluginErrorCode.NotFound, "The remembered path no longer exists.");
        }

        string token = _scope.CreateFileGrant(resolved, isDirectory, write: false);
        return new FilesPickResult { Token = token, DisplayPath = resolved, IsDirectory = isDirectory, Entries = [] };
    }

    private object SerialList()
    {
        RequirePermission(Permission.SerialRead);
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

    private object SubscribeSerial(SerialSubscribeRequest request)
    {
        RequirePermission(Permission.SerialRead);
        HostSerialSession session = _environment.GetSerialSessions()
            .FirstOrDefault(candidate => string.Equals(candidate.SessionId, request.SessionId, StringComparison.Ordinal))
            ?? throw new PluginScopeException(PluginErrorCode.NotFound, $"Session '{request.SessionId}' does not exist.");
        if (!session.Open)
        {
            throw new PluginScopeException(PluginErrorCode.InvalidArgument, $"Session '{request.SessionId}' is not open.");
        }

        string subscriptionId = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12));
        SerialSubscriptionAdded?.Invoke(subscriptionId, session.SessionId);
        return new SerialSubscribeResult
        {
            SubscriptionId = subscriptionId,
            QueueBlocks = _scope.Limits.SerialQueueBlocks,
            QueueMaxBytes = _scope.Limits.SerialQueueMaxBytes,
        };
    }

    private object UnsubscribeSerial(FilesTokenRequest request)
    {
        RequirePermission(Permission.SerialRead);
        SerialSubscriptionRemoved?.Invoke(this, request.Token);
        return new { ok = true };
    }

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
                    FileIoTestHook?.Invoke("snapshot.before-copy", snapshotPath);
                    await CopySnapshotFileAsync(file.Path, snapshotPath, file.Length, cancellationToken).ConfigureAwait(false);
                    FileIoTestHook?.Invoke("snapshot.after-copy", snapshotPath);
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
            FileIoTestHook?.Invoke("publish.staging-open", targetStaging);
            await input.CopyToAsync(staged, 64 * 1024, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            staged.Flush(flushToDisk: true);
            FileIoTestHook?.Invoke("publish.before-commit", targetStaging);
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
            try { FileIoTestHook?.Invoke("publish.before-cleanup", targetStaging); }
            finally
            {
                if (!_scope.CleanupResource(publicationResourceId, HostTempResourceKind.PublicationFile, targetStaging, bytes))
                    _diagnostics.Write(PluginLogLevel.Warning, $"Publication staging cleanup is pending for resource {publicationResourceId}, '{Path.GetFileName(targetStaging)}'.");
            }
        }
        return new FilesCommitResult { FinalPath = target.Path, Bytes = bytes };
    }

    private static FileStream CreateSecureStaging(string path)
    {
        const uint GenericRead = 0x80000000;
        const uint GenericWrite = 0x40000000;
        const uint Delete = 0x00010000;
        const uint CreateNew = 1;
        const uint SequentialScan = 0x08000000;
        const uint WriteThrough = 0x80000000;
        const uint Overlapped = 0x40000000;
        const uint OpenReparsePoint = 0x00200000;
        SafeFileHandle handle = CreateFileW(path, GenericRead | GenericWrite | Delete, 0, IntPtr.Zero, CreateNew, SequentialScan | WriteThrough | Overlapped | OpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException("Cannot create secure publication staging file.", new System.ComponentModel.Win32Exception(error));
        }
        return new FileStream(handle, FileAccess.ReadWrite, 64 * 1024, isAsync: true);
    }

    private static void RenameByHandle(SafeFileHandle handle, string destination, bool replaceApproved)
    {
        const int FileRenameInformation = 10;
        string nativeDestination = @"\??\" + Path.GetFullPath(destination);
        byte[] name = System.Text.Encoding.Unicode.GetBytes(nativeDestination);
        int rootOffset = IntPtr.Size == 8 ? 8 : 4;
        int lengthOffset = rootOffset + IntPtr.Size;
        int nameOffset = lengthOffset + sizeof(uint);
        byte[] native = new byte[nameOffset + name.Length];
        IntPtr buffer = Marshal.AllocHGlobal(native.Length);
        try
        {
            Marshal.Copy(native, 0, buffer, native.Length);
            Marshal.WriteByte(buffer, replaceApproved ? (byte)1 : (byte)0);
            Marshal.WriteIntPtr(buffer, rootOffset, IntPtr.Zero);
            Marshal.WriteInt32(buffer, lengthOffset, name.Length);
            Marshal.Copy(name, 0, buffer + nameOffset, name.Length);
            int status = NtSetInformationFile(handle, out _, buffer, (uint)(nameOffset + name.Length), FileRenameInformation);
            if (status < 0)
            {
                int error = unchecked((int)RtlNtStatusToDosError(status));
                if (!replaceApproved && error is 80 or 183)
                    throw new PluginScopeException(PluginErrorCode.PermissionDenied, "The selected target now exists and replacement was not approved.");
                throw new IOException("Secure publication rename failed.", new System.ComponentModel.Win32Exception(error));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        public IntPtr Status;
        public IntPtr Information;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtSetInformationFile(SafeFileHandle fileHandle, out IoStatusBlock ioStatusBlock, IntPtr fileInformation, uint length, int fileInformationClass);

    [DllImport("ntdll.dll")]
    private static extern uint RtlNtStatusToDosError(int status);

    private static void RejectReparsePoints(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new PluginScopeException(PluginErrorCode.PermissionDenied, "Reparse points are not allowed in output paths.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private sealed class DirectoryLease : IDisposable
    {
        private readonly List<SafeFileHandle> _handles = [];

        public DirectoryLease(string path)
        {
            try
            {
                // Fail closed on platforms where directory replacement cannot be pinned here.
                if (!OperatingSystem.IsWindows())
                    throw new PluginScopeException(PluginErrorCode.UnsupportedOperation, "Secure output requires Windows directory handles.");
                Stack<string> parents = new();
                for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
                    parents.Push(current);
                foreach (string current in parents)
                {
                    SafeFileHandle handle = CreateFile(current, 0x80000000, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
                    if (handle.IsInvalid)
                    {
                        handle.Dispose();
                        throw new PluginScopeException(PluginErrorCode.PermissionDenied, "Cannot secure the output directory.");
                    }
                    _handles.Add(handle);
                    RejectReparsePoints(current);
                }
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            foreach (SafeFileHandle handle in _handles) handle.Dispose();
            _handles.Clear();
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
        private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    }

    private object UpdateUi(UiUpdateRequest request)
    {
        if (!_scope.HasCapability(Capability.ToolPage))
        {
            throw new PluginScopeException(PluginErrorCode.PermissionDenied, "The manifest does not declare tool-page.");
        }

        if (!UiContributionValidator.Validate(request.Nodes, out string? error))
        {
            throw new PluginScopeException(PluginErrorCode.InvalidArgument, error ?? "Invalid UI update.");
        }

        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        lock (_uiWindow)
        {
            while (_uiWindow.Count > 0 && _uiWindow.Peek() <= now - 60_000) _uiWindow.Dequeue();
            if (_uiWindow.Count >= _scope.Limits.UiUpdatesPerMinute)
                throw new PluginScopeException(PluginErrorCode.ResourceLimit, "UI update rate limit exceeded.");
            _uiWindow.Enqueue(now);
        }
        _environment.UpdateToolPage(_scope.Manifest.Id, request.ContributionId, request.Nodes);
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

    private object BackgroundSet(BackgroundSetRequest request)
    {
        if (!_scope.HasCapability(Capability.BackgroundImage))
        {
            throw new PluginScopeException(PluginErrorCode.PermissionDenied, "The manifest does not declare background-image.");
        }

        string? path = null;
        if (request.Token is { Length: > 0 })
        {
            FileGrant grant = _scope.ResolveToken(request.Token, write: false)
                ?? throw new PluginScopeException(PluginErrorCode.SessionExpired, "The image token is invalid or expired.");
            path = grant.Path;
        }

        double? opacity = request.Opacity is null ? null : Math.Clamp(request.Opacity.Value, 0d, 1d);
        _environment.ApplyBackground(new BackgroundApply { PluginId = _scope.Manifest.Id, ImageTokenPath = path, Opacity = opacity });
        return new { ok = true };
    }

    private object BackgroundClear()
    {
        if (!_scope.HasCapability(Capability.BackgroundImage))
        {
            throw new PluginScopeException(PluginErrorCode.PermissionDenied, "The manifest does not declare background-image.");
        }

        _environment.ApplyBackground(new BackgroundApply { PluginId = _scope.Manifest.Id, ImageTokenPath = null, Opacity = null });
        return new { ok = true };
    }

    private FileGrant ResolveGrant(string token, bool write)
    {
        return _scope.ResolveToken(token, write)
            ?? throw new PluginScopeException(PluginErrorCode.SessionExpired, "The file token is invalid or expired.");
    }

    private const string PermissionDeniedCode = PluginErrorCode.PermissionDenied;

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
        }
    }

    private static bool TryDeleteConfirmed(string path)
    {
        TryDelete(path);
        return !File.Exists(path);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                RejectReparsePoints(path);
                Directory.Delete(path, recursive: false);
            }
        }
        catch (Exception)
        {
        }
    }
}
