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
public sealed partial class PluginBroker : IDisposable
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
    private readonly NativeHelperTaskManager _helperTasks;
    private readonly Action<string, string>? _fileIoCheckpoint;

    public PluginBroker(ActivationScope scope, IPluginHostEnvironment environment, PluginDiagnosticsLog diagnostics, string? packageDirectory = null)
        : this(scope, environment, diagnostics, packageDirectory, null)
    {
    }

    internal PluginBroker(
        ActivationScope scope,
        IPluginHostEnvironment environment,
        PluginDiagnosticsLog diagnostics,
        string? packageDirectory,
        Action<string, string>? fileIoCheckpoint)
    {
        _scope = scope ?? throw new ArgumentNullException(nameof(scope));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _fileIoCheckpoint = fileIoCheckpoint;
        _storageFilePath = Path.Combine(scope.StorageDirectory, "config.json");
        _diagAllowancePerMinute = Math.Max(scope.Limits.DiagMessagesPerMinute, 1);
        _helperTasks = new NativeHelperTaskManager(scope.Manifest, packageDirectory ?? scope.TempDirectory, Path.Combine(scope.TempDirectory, "HelperTasks"));
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
            PluginOps.FilesSnapshot => Json(await SnapshotFilesAsync(Require<FilesSnapshotRequest>(data), cancellationToken)),
            PluginOps.SerialList => Json(SerialList()),
            PluginOps.SerialPorts => Json(SerialPorts()),
            PluginOps.SerialSubscribe => Json(SubscribeSerial(Require<SerialSubscribeRequest>(data))),
            PluginOps.SerialUnsubscribe => Json(UnsubscribeSerial(Require<FilesTokenRequest>(data))),
            PluginOps.HelperStart => Json(StartHelper(Require<HelperStartRequest>(data))),
            PluginOps.HelperStatus => Json(HelperStatus(Require<HelperTaskRequest>(data))),
            PluginOps.HelperCancel => Json(await CancelHelperAsync(Require<HelperTaskRequest>(data))),
            PluginOps.SerialLeaseAcquire => Json(await AcquireSerialLeaseAsync(Require<SerialLeaseAcquireRequest>(data), cancellationToken)),
            PluginOps.SerialLeaseRelease => Json(await ReleaseSerialLeaseAsync(Require<SerialLeaseRequest>(data), cancellationToken)),
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

    public void Dispose()
    {
        _helperTasks.Dispose();
        _environment.RevokeSerialLeases(_scope.Manifest.Id, _scope.ActivationId);
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

}
