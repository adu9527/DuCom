using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DuCom.Plugin;

public static class PluginErrorCode
{
    public const string InvalidArgument = "InvalidArgument";
    public const string PermissionDenied = "PermissionDenied";
    public const string Cancelled = "Cancelled";
    public const string NotFound = "NotFound";
    public const string ResourceLimit = "ResourceLimit";
    public const string DeadlineExceeded = "DeadlineExceeded";
    public const string UnsupportedOperation = "UnsupportedOperation";
    public const string SessionExpired = "SessionExpired";
    public const string InternalError = "InternalError";
}

public sealed record PluginWireError
{
    [JsonPropertyName("code")] public string Code { get; init; } = PluginErrorCode.InternalError;
    [JsonPropertyName("message")] public string Message { get; init; } = string.Empty;
    [JsonPropertyName("detail")] public string? Detail { get; init; }
}

public sealed record PluginWireMessage
{
    [JsonPropertyName("v")] public string ProtocolVersion { get; init; } = "1.0";
    [JsonPropertyName("sid")] public string SessionId { get; init; } = string.Empty;
    [JsonPropertyName("aid")] public string ActivationId { get; init; } = string.Empty;
    [JsonPropertyName("k")] public string Kind { get; init; } = WireKinds.Request;
    [JsonPropertyName("id")] public string? RequestId { get; init; }
    [JsonPropertyName("op")] public string Operation { get; init; } = string.Empty;
    [JsonPropertyName("d")] public JsonElement? Data { get; init; }
    [JsonPropertyName("e")] public PluginWireError? Error { get; init; }

    public static PluginWireMessage Request(string sessionId, string activationId, string requestId, string operation, JsonElement? data = null) =>
        new() { SessionId = sessionId, ActivationId = activationId, Kind = WireKinds.Request, RequestId = requestId, Operation = operation, Data = data };

    public static PluginWireMessage Response(string sessionId, string activationId, string requestId, JsonElement? data) =>
        new() { SessionId = sessionId, ActivationId = activationId, Kind = WireKinds.Response, RequestId = requestId, Operation = string.Empty, Data = data };

    public static PluginWireMessage ErrorResponse(string sessionId, string activationId, string requestId, string code, string message) =>
        new() { SessionId = sessionId, ActivationId = activationId, Kind = WireKinds.Response, RequestId = requestId, Operation = string.Empty, Error = new PluginWireError { Code = code, Message = message } };

    public static PluginWireMessage Notify(string sessionId, string activationId, string operation, JsonElement? data = null) =>
        new() { SessionId = sessionId, ActivationId = activationId, Kind = WireKinds.Notification, Operation = operation, Data = data };
}

public static class WireKinds
{
    public const string Request = "req";
    public const string Response = "res";
    public const string Notification = "nfy";
}

public static class PluginOps
{
    public const string WorkerHello = "worker.hello";
    public const string WorkerInit = "worker.init";
    public const string WorkerHeartbeat = "worker.heartbeat";
    public const string PluginActivate = "plugin.activate";
    public const string PluginDeactivate = "plugin.deactivate";
    public const string CommandInvoke = "command.invoke";
    public const string SettingsApply = "settings.apply";
    public const string TaskCancel = "task.cancel";
    public const string TaskProgress = "task.progress";
    public const string SerialData = "serial.data";
    public const string SessionClosed = "session.closed";
    public const string StorageRead = "storage.read";
    public const string StorageWrite = "storage.write";
    public const string LogDiag = "log.diag";
    public const string FilesPickRead = "files.pickRead";
    public const string FilesPickWrite = "files.pickWrite";
    public const string FilesHostPaths = "files.hostPaths";
    public const string FilesCreateWriteTarget = "files.createWriteTarget";
    public const string FilesStat = "files.stat";
    public const string FilesRead = "files.read";
    public const string FilesList = "files.list";
    public const string FilesRemembered = "files.remembered";
    public const string FilesSnapshot = "files.snapshot";
    public const string SerialList = "serial.list";
    public const string SerialPorts = "serial.ports";
    public const string SerialSubscribe = "serial.subscribe";
    public const string SerialUnsubscribe = "serial.unsubscribe";
    public const string HelperStart = "helper.start";
    public const string HelperStatus = "helper.status";
    public const string HelperCancel = "helper.cancel";
    public const string SerialLeaseAcquire = "serial.lease.acquire";
    public const string SerialLeaseRelease = "serial.lease.release";
    public const string LogsList = "logs.list";
    public const string LogsSnapshot = "logs.snapshot";
    public const string LogsRead = "logs.read";
    public const string LogsRelease = "logs.release";
    public const string OutputBegin = "output.begin";
    public const string OutputWrite = "output.write";
    public const string OutputCommit = "output.commit";
    public const string OutputCommitStatus = "output.commitStatus";
    public const string OutputDiscard = "output.discard";
    public const string UiUpdate = "ui.update";
    public const string UiNotify = "ui.notify";
    public const string BackgroundSet = "background.set";
    public const string BackgroundClear = "background.clear";
}

public static class PluginWire
{
    public const int MaximumFrameBytes = 1024 * 1024;
    public const int MaximumHeaderBytes = 4;
    public const string ProtocolVersion = "1.0";

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static byte[] Encode(PluginWireMessage message)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        if (payload.Length > MaximumFrameBytes)
        {
            throw new InvalidOperationException($"Encoded frame of {payload.Length} bytes exceeds the {MaximumFrameBytes} byte limit.");
        }

        byte[] frame = new byte[MaximumHeaderBytes + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)payload.Length);
        payload.AsSpan().CopyTo(frame.AsSpan(MaximumHeaderBytes));
        return frame;
    }

    public static async Task<PluginWireMessage?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] header = new byte[MaximumHeaderBytes];
        if (!await ReadExactlyAsync(stream, header, cancellationToken))
        {
            return null;
        }

        uint length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length > MaximumFrameBytes)
        {
            throw new InvalidOperationException($"Frame length {length} exceeds the {MaximumFrameBytes} byte limit.");
        }

        byte[] payload = new byte[length];
        if (length > 0 && !await ReadExactlyAsync(stream, payload, cancellationToken))
        {
            return null;
        }

        return JsonSerializer.Deserialize<PluginWireMessage>(payload, JsonOptions);
    }

    public static async Task WriteAsync(Stream stream, PluginWireMessage message, CancellationToken cancellationToken)
    {
        byte[] frame = Encode(message);
        await stream.WriteAsync(frame, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async Task<bool> ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken);
            if (read == 0)
            {
                return false;
            }

            total += read;
        }

        return true;
    }
}
