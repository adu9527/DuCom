using System.Text.Json;
using DuCom.Plugin.Dto;

namespace DuCom.Plugin;

internal static class OpTimeouts
{
    public static readonly TimeSpan UserInteraction = Timeout.InfiniteTimeSpan;
    public static readonly TimeSpan Standard = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan Chunk = TimeSpan.FromSeconds(120);

    public static TimeSpan For(string operation) => operation switch
    {
        PluginOps.FilesPickRead or PluginOps.FilesPickWrite or PluginOps.UiNotify => UserInteraction,
        PluginOps.LogsSnapshot => TimeSpan.FromSeconds(30),
        PluginOps.OutputCommit or PluginOps.OutputWrite => Chunk,
        PluginOps.HelperStart or PluginOps.HelperCancel => Chunk,
        PluginOps.FilesRead or PluginOps.LogsRead => Chunk,
        PluginOps.FilesSnapshot => Chunk,
        _ => Standard,
    };
}

internal interface ISerialEventSink
{
    void RaiseData(SerialDataEventArgs args);

    void RaiseClosed(string sessionId);
}

internal sealed class PluginApiFacades(IPluginRequestChannel channel) : IPluginHostApi
{
    public string PluginId { get; internal set; } = string.Empty;
    public string PluginVersion { get; internal set; } = string.Empty;
    public string Culture { get; internal set; } = "en-US";
    public PluginLimits Limits { get; internal set; } = new();
    public IReadOnlyList<string> GrantedCapabilities { get; internal set; } = [];
    public IReadOnlyList<string> GrantedPermissions { get; internal set; } = [];

    public bool HasPermission(string permission) => GrantedPermissions.Contains(permission, StringComparer.Ordinal);

    public IPluginStorage Storage { get; } = new StorageFacade(channel);
    public IPluginFiles Files { get; } = new FilesFacade(channel);
    public IPluginSerial Serial { get; } = new SerialFacade(channel);
    public IPluginLogs Logs { get; } = new LogsFacade(channel);
    public IPluginOutput Output { get; } = new OutputFacade(channel);
    public IPluginHelpers Helpers { get; } = new HelpersFacade(channel);
    public IPluginUi Ui { get; } = new UiFacade(channel);
    public IPluginBackground Background { get; } = new BackgroundFacade(channel);
    public IPluginDiagnostics Diagnostics { get; } = new DiagnosticsFacade(channel);

    internal ISerialEventSink SerialSink => (ISerialEventSink)Serial;

    private static async Task<JsonElement?> RequestAsync(IPluginRequestChannel channel, string op, object? payload, CancellationToken ct)
    {
        using CancellationTokenSource timeout = new(OpTimeouts.For(op));
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        return await channel.RequestAsync(op, payload, linked.Token);
    }

    private static async Task<T?> RequestTypedAsync<T>(IPluginRequestChannel channel, string op, object? payload, CancellationToken ct)
    {
        JsonElement? result = await RequestAsync(channel, op, payload, ct);
        if (result is null || result.Value.ValueKind == JsonValueKind.Null)
        {
            return default;
        }

        return result.Value.Deserialize<T>(DtoJson.Options);
    }

    private sealed class StorageFacade(IPluginRequestChannel channel) : IPluginStorage
    {
        public async Task<string?> ReadAsync(CancellationToken cancellationToken = default)
        {
            StorageReadResult? result = await RequestTypedAsync<StorageReadResult>(channel, PluginOps.StorageRead, null, cancellationToken);
            return result?.Data;
        }

        public async Task WriteAsync(string json, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(json);
            await RequestAsync(channel, PluginOps.StorageWrite, new StorageWriteRequest { Data = json }, cancellationToken);
        }
    }

    private sealed class FilesFacade(IPluginRequestChannel channel) : IPluginFiles
    {
        public async Task<FilesPickResult?> PickReadAsync(FilePickReadOptions options, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(options);
            return await RequestTypedAsync<FilesPickResult>(channel, PluginOps.FilesPickRead, new FilesPickReadRequest
            {
                Mode = options.Mode,
                FilterName = options.FilterName,
                Extensions = options.Extensions ?? [],
                Remember = options.Remember,
            }, cancellationToken);
        }

        public async Task<FilesPickResult?> PickWriteAsync(FilePickWriteOptions options, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(options);
            return await RequestTypedAsync<FilesPickResult>(channel, PluginOps.FilesPickWrite, new FilesPickWriteRequest
            {
                Mode = options.Mode,
                SuggestName = options.SuggestName,
                FilterName = options.FilterName,
                Extensions = options.Extensions ?? [],
            }, cancellationToken);
        }

        public async Task<HostPathsResult> GetHostPathsAsync(CancellationToken cancellationToken = default)
        {
            HostPathsResult? result = await RequestTypedAsync<HostPathsResult>(channel, PluginOps.FilesHostPaths, null, cancellationToken);
            return result ?? new HostPathsResult();
        }

        public Task<FilesPickResult?> CreateWriteTargetAsync(string? directory, string suggestName, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrEmpty(suggestName);
            return RequestTypedAsync<FilesPickResult>(channel, PluginOps.FilesCreateWriteTarget, new FilesCreateWriteTargetRequest
            {
                Directory = string.IsNullOrWhiteSpace(directory) ? null : directory,
                SuggestName = suggestName,
            }, cancellationToken);
        }

        public async Task<string?> RequestRememberedReadTokenAsync(string path, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrEmpty(path);
            JsonElement? response = await RequestAsync(channel, PluginOps.FilesRemembered, new FilesRememberedRequest { Path = path }, cancellationToken);
            if (response is null)
            {
                return null;
            }

            return response.Value.TryGetProperty("token", out JsonElement token) ? token.GetString() : null;
        }

        public async Task<FilesStatResult> StatAsync(string token, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrEmpty(token);
            return await RequestTypedAsync<FilesStatResult>(channel, PluginOps.FilesStat, new FilesStatRequest { Token = token }, cancellationToken)
                ?? throw new PluginHostException(PluginErrorCode.InternalError, "Missing stat response.");
        }

        public async Task<FileReadChunk> ReadChunkAsync(string token, long offset, int length, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrEmpty(token);
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            length = Math.Clamp(length, 1, 512 * 1024);
            FilesReadResult? result = await RequestTypedAsync<FilesReadResult>(channel, PluginOps.FilesRead, new FilesReadRequest { Token = token, Offset = offset, Length = length }, cancellationToken);
            if (result is null)
            {
                throw new PluginHostException(PluginErrorCode.InternalError, "Missing read response.");
            }

            byte[] data = string.IsNullOrEmpty(result.B64) ? [] : Convert.FromBase64String(result.B64);
            return new FileReadChunk(data, offset + data.Length);
        }

        public async Task<IReadOnlyList<PickedEntry>> ListAsync(string token, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrEmpty(token);
            FilesListResult? result = await RequestTypedAsync<FilesListResult>(channel, PluginOps.FilesList, new FilesListRequest { Token = token }, cancellationToken);
            return result?.Entries ?? [];
        }

        public async Task<IReadOnlyList<FileSnapshotEntry>> CreateTaskSnapshotsAsync(string taskId, IReadOnlyList<string> tokens, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrEmpty(taskId);
            ArgumentNullException.ThrowIfNull(tokens);
            FilesSnapshotResult? result = await RequestTypedAsync<FilesSnapshotResult>(channel, PluginOps.FilesSnapshot, new FilesSnapshotRequest { TaskId = taskId, Tokens = tokens }, cancellationToken);
            return result?.Files ?? [];
        }

    }

    private sealed class SerialFacade(IPluginRequestChannel channel) : IPluginSerial, ISerialEventSink, IDisposable
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<string, byte> _subscriptions = new(StringComparer.Ordinal);

        public event EventHandler<SerialDataEventArgs>? DataReceived;
        public event EventHandler<string>? SessionClosed;

        void ISerialEventSink.RaiseData(SerialDataEventArgs args) => DataReceived?.Invoke(this, args);
        void ISerialEventSink.RaiseClosed(string sessionId) => SessionClosed?.Invoke(this, sessionId);

        public async Task<IReadOnlyList<SerialSessionInfo>> ListSessionsAsync(CancellationToken cancellationToken = default)
        {
            SerialListResult? result = await RequestTypedAsync<SerialListResult>(channel, PluginOps.SerialList, null, cancellationToken);
            return result?.Sessions ?? [];
        }

        public async Task<IReadOnlyList<SerialPortInfo>> ListPortsAsync(CancellationToken cancellationToken = default)
        {
            SerialPortsResult? result = await RequestTypedAsync<SerialPortsResult>(channel, PluginOps.SerialPorts, null, cancellationToken);
            return result?.Ports ?? [];
        }

        public async Task<bool> SubscribeAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrEmpty(sessionId);
            SerialSubscribeResult? result = await RequestTypedAsync<SerialSubscribeResult>(channel, PluginOps.SerialSubscribe, new SerialSubscribeRequest { SessionId = sessionId }, cancellationToken);
            if (result is null)
            {
                return false;
            }

            lock (_gate)
            {
                _subscriptions[result.SubscriptionId] = 0;
            }

            return true;
        }

        public async Task UnsubscribeAllAsync(CancellationToken cancellationToken = default)
        {
            string[] subscriptionIds;
            lock (_gate)
            {
                subscriptionIds = [.. _subscriptions.Keys];
                _subscriptions.Clear();
            }

            foreach (string subscriptionId in subscriptionIds)
            {
                await RequestAsync(channel, PluginOps.SerialUnsubscribe, new FilesTokenRequest { Token = subscriptionId }, cancellationToken);
            }
        }

        public Task<SerialLeaseResult> AcquireLeaseAsync(string taskId, string port, string? deviceIdentity, bool restoreSession, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrEmpty(taskId);
            ArgumentException.ThrowIfNullOrEmpty(port);
            return RequestTypedAsync<SerialLeaseResult>(channel, PluginOps.SerialLeaseAcquire, new SerialLeaseAcquireRequest
            {
                TaskId = taskId,
                Port = port,
                DeviceIdentity = deviceIdentity,
                RestoreSession = restoreSession,
            }, cancellationToken)!;
        }

        public Task<SerialLeaseResult> ReleaseLeaseAsync(string leaseId, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrEmpty(leaseId);
            return RequestTypedAsync<SerialLeaseResult>(channel, PluginOps.SerialLeaseRelease, new SerialLeaseRequest { LeaseId = leaseId }, cancellationToken)!;
        }

        public void Dispose()
        {
        }
    }

    private sealed class LogsFacade(IPluginRequestChannel channel) : IPluginLogs
    {
        public async Task<IReadOnlyList<SerialSessionInfo>> ListSessionsAsync(CancellationToken cancellationToken = default)
        {
            SerialListResult? result = await RequestTypedAsync<SerialListResult>(channel, PluginOps.LogsList, null, cancellationToken);
            return result?.Sessions ?? [];
        }

        public async Task<LogSnapshot?> CreateSnapshotAsync(string? sessionId, CancellationToken cancellationToken = default)
        {
            LogsSnapshotResult? result = await RequestTypedAsync<LogsSnapshotResult>(channel, PluginOps.LogsSnapshot, new LogsSnapshotRequest { SessionId = sessionId }, cancellationToken);
            if (result is null)
            {
                return null;
            }

            return new LogSnapshot(result.Token, [.. result.Files.Select(file => new LogSnapshotFileEntry(file.Index, file.Name, file.Length, file.Port))]);
        }

        public async Task<FileReadChunk> ReadChunkAsync(string snapshotToken, int fileIndex, long offset, int length, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrEmpty(snapshotToken);
            length = Math.Clamp(length, 1, 512 * 1024);
            LogsReadResult? result = await RequestTypedAsync<LogsReadResult>(channel, PluginOps.LogsRead, new LogsReadRequest { Token = snapshotToken, Index = fileIndex, Offset = offset, Length = length }, cancellationToken);
            if (result is null)
            {
                throw new PluginHostException(PluginErrorCode.InternalError, "Missing log read response.");
            }

            byte[] data = string.IsNullOrEmpty(result.B64) ? [] : Convert.FromBase64String(result.B64);
            return new FileReadChunk(data, offset + data.Length);
        }

        public Task ReleaseSnapshotAsync(string snapshotToken, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrEmpty(snapshotToken);
            return RequestAsync(channel, PluginOps.LogsRelease, new FilesTokenRequest { Token = snapshotToken }, cancellationToken);
        }
    }

    private sealed class OutputFacade(IPluginRequestChannel channel) : IPluginOutput
    {
        public async Task<OutputHandle> BeginAsync(CancellationToken cancellationToken = default)
        {
            OutputBeginResult? result = await RequestTypedAsync<OutputBeginResult>(channel, PluginOps.OutputBegin, null, cancellationToken);
            return result is null
                ? throw new PluginHostException(PluginErrorCode.InternalError, "Missing output.begin response.")
                : new OutputHandle(result.Token, result.MaxBytes, result.ChunkMaxBytes);
        }

        public async Task<long> WriteAsync(string outputToken, long offset, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrEmpty(outputToken);
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            if (data.Length == 0 || data.Length > 384 * 1024)
                throw new ArgumentOutOfRangeException(nameof(data), "Output chunks must be between 1 byte and 384 KiB.");
            OutputWriteResult? result = await RequestTypedAsync<OutputWriteResult>(channel, PluginOps.OutputWrite,
                new OutputWriteRequest { Token = outputToken, Offset = offset, B64 = Convert.ToBase64String(data.Span) }, cancellationToken);
            return result?.Length ?? throw new PluginHostException(PluginErrorCode.InternalError, "Missing output.write response.");
        }

        public Task<FilesCommitResult> CommitAsync(string outputToken, string targetWriteToken, string commitId, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrEmpty(outputToken);
            ArgumentException.ThrowIfNullOrEmpty(targetWriteToken);
            ArgumentException.ThrowIfNullOrEmpty(commitId);
            return RequestTypedAsync<FilesCommitResult>(channel, PluginOps.OutputCommit, new OutputCommitRequest { Token = outputToken, TargetToken = targetWriteToken, CommitId = commitId }, cancellationToken)!;
        }

        public async Task<OutputCommitStatus> GetCommitStatusAsync(string commitId, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrEmpty(commitId);
            OutputCommitStatusResult? result = await RequestTypedAsync<OutputCommitStatusResult>(channel, PluginOps.OutputCommitStatus, new OutputCommitStatusRequest { CommitId = commitId }, cancellationToken);
            return result is null
                ? new OutputCommitStatus("unknown", null, 0, PluginErrorCode.InternalError)
                : new OutputCommitStatus(result.State, result.FinalPath, result.Bytes, result.ErrorCode);
        }

        public Task DiscardAsync(string outputToken, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrEmpty(outputToken);
            return RequestAsync(channel, PluginOps.OutputDiscard, new FilesTokenRequest { Token = outputToken }, cancellationToken);
        }
    }

    private sealed class HelpersFacade(IPluginRequestChannel channel) : IPluginHelpers
    {
        public Task<HelperTaskResult> StartAsync(string helperId, string taskId, string payload, int timeoutMs, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrEmpty(helperId);
            ArgumentException.ThrowIfNullOrEmpty(taskId);
            ArgumentNullException.ThrowIfNull(payload);
            return RequestTypedAsync<HelperTaskResult>(channel, PluginOps.HelperStart, new HelperStartRequest
            {
                HelperId = helperId,
                TaskId = taskId,
                Payload = payload,
                TimeoutMs = timeoutMs,
            }, cancellationToken)!;
        }

        public Task<HelperTaskResult> GetStatusAsync(string taskId, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrEmpty(taskId);
            return RequestTypedAsync<HelperTaskResult>(channel, PluginOps.HelperStatus, new HelperTaskRequest { TaskId = taskId }, cancellationToken)!;
        }

        public Task<HelperTaskResult> CancelAsync(string taskId, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrEmpty(taskId);
            return RequestTypedAsync<HelperTaskResult>(channel, PluginOps.HelperCancel, new HelperTaskRequest { TaskId = taskId }, cancellationToken)!;
        }
    }

    private sealed class UiFacade(IPluginRequestChannel channel) : IPluginUi
    {
        public Task UpdateToolPageAsync(string contributionId, IReadOnlyList<UiNode> nodes, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrEmpty(contributionId);
            ArgumentNullException.ThrowIfNull(nodes);
            if (!UiContributionValidator.Validate(nodes, out string? error))
            {
                throw new ArgumentException(error, nameof(nodes));
            }

            return RequestAsync(channel, PluginOps.UiUpdate, new UiUpdateRequest { ContributionId = contributionId, Nodes = nodes }, cancellationToken);
        }

        public Task NotifyAsync(string title, string message, string? revealPath, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrEmpty(title);
            ArgumentException.ThrowIfNullOrEmpty(message);
            return RequestAsync(channel, PluginOps.UiNotify, new UiNotifyRequest
            {
                Title = title.Length > 200 ? title[..200] : title,
                Message = message.Length > 4000 ? message[..4000] : message,
                RevealPath = revealPath,
            }, cancellationToken);
        }
    }

    private sealed class BackgroundFacade(IPluginRequestChannel channel) : IPluginBackground
    {
        public Task SetAsync(string? imageToken, double? opacity, CancellationToken cancellationToken = default)
        {
            if (opacity.HasValue)
            {
                opacity = Math.Clamp(opacity.Value, 0d, 1d);
            }

            return RequestAsync(channel, PluginOps.BackgroundSet, new BackgroundSetRequest { Token = imageToken, Opacity = opacity }, cancellationToken);
        }

        public Task ClearAsync(CancellationToken cancellationToken = default) =>
            RequestAsync(channel, PluginOps.BackgroundClear, null, cancellationToken);
    }

    private sealed class DiagnosticsFacade(IPluginRequestChannel channel) : IPluginDiagnostics
    {
        public void Info(string message) => Send("info", message);
        public void Warning(string message) => Send("warning", message);
        public void LogError(string message) => Send("error", message);

        private void Send(string level, string message)
        {
            if (string.IsNullOrEmpty(message))
            {
                return;
            }

            if (message.Length > 2000)
            {
                message = message[..2000];
            }

            _ = NotifySafeAsync(level, message);
        }

        private async Task NotifySafeAsync(string level, string message)
        {
            try
            {
                await channel.NotifyAsync(PluginOps.LogDiag, new LogDiagNotice { Level = level, Message = message }, CancellationToken.None);
            }
            catch (Exception)
            {
            }
        }
    }
}



