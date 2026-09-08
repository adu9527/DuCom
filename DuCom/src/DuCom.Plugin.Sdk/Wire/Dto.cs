using System.Text.Json.Serialization;

namespace DuCom.Plugin.Dto;

public static class DtoJson
{
    public static readonly System.Text.Json.JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

public sealed record StorageReadResult
{
    [JsonPropertyName("data")] public string? Data { get; init; }
    [JsonPropertyName("bytes")] public long Bytes { get; init; }
}

public sealed record StorageWriteRequest
{
    [JsonPropertyName("data")] public string Data { get; init; } = string.Empty;
}

public sealed record LogDiagNotice
{
    [JsonPropertyName("level")] public string Level { get; init; } = "info";
    [JsonPropertyName("message")] public string Message { get; init; } = string.Empty;
}

public sealed record FilesPickReadRequest
{
    [JsonPropertyName("mode")] public string Mode { get; init; } = "file";
    [JsonPropertyName("filterName")] public string? FilterName { get; init; }
    [JsonPropertyName("extensions")] public IReadOnlyList<string> Extensions { get; init; } = [];
    [JsonPropertyName("remember")] public bool Remember { get; init; }
}

public sealed record FilesPickWriteRequest
{
    /// <summary>"saveFile" (default) opens a save dialog; "directory" opens a folder picker whose grant is remembered.</summary>
    [JsonPropertyName("mode")] public string Mode { get; init; } = "saveFile";
    [JsonPropertyName("suggestName")] public string SuggestName { get; init; } = "output.zip";
    [JsonPropertyName("filterName")] public string? FilterName { get; init; }
    [JsonPropertyName("extensions")] public IReadOnlyList<string> Extensions { get; init; } = [];
}

public sealed record HostPathsResult
{
    [JsonPropertyName("logDirectory")] public string LogDirectory { get; init; } = string.Empty;
}

public sealed record FilesCreateWriteTargetRequest
{
    /// <summary>null or empty selects the host-managed log directory; otherwise a directory the user granted to this plugin.</summary>
    [JsonPropertyName("directory")] public string? Directory { get; init; }
    [JsonPropertyName("suggestName")] public string SuggestName { get; init; } = "output.bin";
}

public sealed record UiNotifyRequest
{
    [JsonPropertyName("title")] public string Title { get; init; } = string.Empty;
    [JsonPropertyName("message")] public string Message { get; init; } = string.Empty;
    [JsonPropertyName("revealPath")] public string? RevealPath { get; init; }
}

public sealed record PickedEntry
{
    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
    [JsonPropertyName("isDirectory")] public bool IsDirectory { get; init; }
    [JsonPropertyName("length")] public long Length { get; init; }
}

public sealed record FilesPickResult
{
    [JsonPropertyName("token")] public string Token { get; init; } = string.Empty;
    [JsonPropertyName("displayPath")] public string DisplayPath { get; init; } = string.Empty;
    [JsonPropertyName("isDirectory")] public bool IsDirectory { get; init; }
    [JsonPropertyName("entries")] public IReadOnlyList<PickedEntry> Entries { get; init; } = [];
    [JsonPropertyName("remembered")] public bool Remembered { get; init; }
}

public sealed record FilesStatRequest
{
    [JsonPropertyName("token")] public string Token { get; init; } = string.Empty;
}

public sealed record FilesStatResult
{
    [JsonPropertyName("exists")] public bool Exists { get; init; }
    [JsonPropertyName("length")] public long Length { get; init; }
}

public sealed record FilesReadRequest
{
    [JsonPropertyName("token")] public string Token { get; init; } = string.Empty;
    [JsonPropertyName("offset")] public long Offset { get; init; }
    [JsonPropertyName("length")] public int Length { get; init; }
}

public sealed record FilesReadResult
{
    [JsonPropertyName("b64")] public string B64 { get; init; } = string.Empty;
    [JsonPropertyName("length")] public int Length { get; init; }
}

public sealed record FilesListRequest
{
    [JsonPropertyName("token")] public string Token { get; init; } = string.Empty;
}

public sealed record FilesListResult
{
    [JsonPropertyName("entries")] public IReadOnlyList<PickedEntry> Entries { get; init; } = [];
}

public sealed record FilesTokenRequest
{
    [JsonPropertyName("token")] public string Token { get; init; } = string.Empty;
}

public sealed record FilesRememberedRequest
{
    [JsonPropertyName("path")] public string Path { get; init; } = string.Empty;
}

public sealed record FilesCommitResult
{
    [JsonPropertyName("finalPath")] public string FinalPath { get; init; } = string.Empty;
    [JsonPropertyName("bytes")] public long Bytes { get; init; }
}

public sealed record SerialSessionInfo
{
    [JsonPropertyName("sessionId")] public string SessionId { get; init; } = string.Empty;
    [JsonPropertyName("port")] public string Port { get; init; } = string.Empty;
    [JsonPropertyName("open")] public bool Open { get; init; }
}

public sealed record SerialListResult
{
    [JsonPropertyName("sessions")] public IReadOnlyList<SerialSessionInfo> Sessions { get; init; } = [];
}

public sealed record SerialSubscribeRequest
{
    [JsonPropertyName("sessionId")] public string SessionId { get; init; } = string.Empty;
}

public sealed record SerialSubscribeResult
{
    [JsonPropertyName("subscriptionId")] public string SubscriptionId { get; init; } = string.Empty;
    [JsonPropertyName("queueBlocks")] public int QueueBlocks { get; init; }
    [JsonPropertyName("queueMaxBytes")] public long QueueMaxBytes { get; init; }
}

public sealed record SerialDataNotice
{
    [JsonPropertyName("sub")] public string SubscriptionId { get; init; } = string.Empty;
    [JsonPropertyName("sessionId")] public string SessionId { get; init; } = string.Empty;
    [JsonPropertyName("seq")] public long Sequence { get; init; }
    [JsonPropertyName("offset")] public long ByteOffset { get; init; }
    [JsonPropertyName("tsUtc")] public DateTimeOffset ReceivedAtUtc { get; init; }
    [JsonPropertyName("b64")] public string B64 { get; init; } = string.Empty;
    [JsonPropertyName("gapFrom")] public long? GapFrom { get; init; }
}

public sealed record SessionClosedNotice
{
    [JsonPropertyName("sessionId")] public string SessionId { get; init; } = string.Empty;
}

public sealed record LogsSnapshotRequest
{
    [JsonPropertyName("sessionId")] public string? SessionId { get; init; }
}

public sealed record LogSnapshotFile
{
    [JsonPropertyName("index")] public int Index { get; init; }
    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
    [JsonPropertyName("length")] public long Length { get; init; }
    [JsonPropertyName("port")] public string Port { get; init; } = string.Empty;
}

public sealed record LogsSnapshotResult
{
    [JsonPropertyName("token")] public string Token { get; init; } = string.Empty;
    [JsonPropertyName("files")] public IReadOnlyList<LogSnapshotFile> Files { get; init; } = [];
    [JsonPropertyName("sessionCount")] public int SessionCount { get; init; }
}

public sealed record LogsReadRequest
{
    [JsonPropertyName("token")] public string Token { get; init; } = string.Empty;
    [JsonPropertyName("index")] public int Index { get; init; }
    [JsonPropertyName("offset")] public long Offset { get; init; }
    [JsonPropertyName("length")] public int Length { get; init; }
}

public sealed record LogsReadResult
{
    [JsonPropertyName("b64")] public string B64 { get; init; } = string.Empty;
    [JsonPropertyName("length")] public int Length { get; init; }
}

public sealed record OutputBeginResult
{
    [JsonPropertyName("token")] public string Token { get; init; } = string.Empty;
    [JsonPropertyName("maxBytes")] public long MaxBytes { get; init; }
    [JsonPropertyName("chunkMaxBytes")] public int ChunkMaxBytes { get; init; }
}

public sealed record OutputWriteRequest
{
    [JsonPropertyName("token")] public string Token { get; init; } = string.Empty;
    [JsonPropertyName("offset")] public long Offset { get; init; }
    [JsonPropertyName("b64")] public string B64 { get; init; } = string.Empty;
}

public sealed record OutputWriteResult
{
    [JsonPropertyName("length")] public long Length { get; init; }
}

public sealed record OutputCommitRequest
{
    [JsonPropertyName("token")] public string Token { get; init; } = string.Empty;
    [JsonPropertyName("targetToken")] public string TargetToken { get; init; } = string.Empty;
    [JsonPropertyName("commitId")] public string CommitId { get; init; } = string.Empty;
}

public sealed record OutputCommitStatusRequest
{
    [JsonPropertyName("commitId")] public string CommitId { get; init; } = string.Empty;
}

public sealed record OutputCommitStatusResult
{
    [JsonPropertyName("state")] public string State { get; init; } = "unknown";
    [JsonPropertyName("finalPath")] public string? FinalPath { get; init; }
    [JsonPropertyName("bytes")] public long Bytes { get; init; }
    [JsonPropertyName("errorCode")] public string? ErrorCode { get; init; }
}

public sealed record CommandInvokeNotice
{
    [JsonPropertyName("commandId")] public string CommandId { get; init; } = string.Empty;
    [JsonPropertyName("arg")] public string? Arg { get; init; }
    [JsonPropertyName("values")] public IReadOnlyDictionary<string, string> Values { get; init; } = new Dictionary<string, string>();
}

public sealed record CommandInvokeResult
{
    [JsonPropertyName("accepted")] public bool Accepted { get; init; }
    [JsonPropertyName("taskId")] public string? TaskId { get; init; }
    [JsonPropertyName("message")] public string? Message { get; init; }
}

public sealed record SettingsApplyNotice
{
    [JsonPropertyName("values")] public IReadOnlyDictionary<string, string> Values { get; init; } = new Dictionary<string, string>();
}

public sealed record TaskCancelNotice
{
    [JsonPropertyName("taskId")] public string TaskId { get; init; } = string.Empty;
}

public sealed record TaskProgressNotice
{
    [JsonPropertyName("taskId")] public string TaskId { get; init; } = string.Empty;
    [JsonPropertyName("percent")] public int? Percent { get; init; }
    [JsonPropertyName("message")] public string? Message { get; init; }
    [JsonPropertyName("processedBytes")] public long? ProcessedBytes { get; init; }
}

public sealed record BackgroundSetRequest
{
    [JsonPropertyName("token")] public string? Token { get; init; }
    [JsonPropertyName("opacity")] public double? Opacity { get; init; }
}

public sealed record UiUpdateRequest
{
    [JsonPropertyName("contributionId")] public string ContributionId { get; init; } = string.Empty;
    [JsonPropertyName("nodes")] public IReadOnlyList<UiNode> Nodes { get; init; } = [];
}

public sealed record HeartbeatNotice
{
    [JsonPropertyName("tsUtc")] public DateTimeOffset TimestampUtc { get; init; }
    [JsonPropertyName("pendingEvents")] public int PendingEvents { get; init; }
    [JsonPropertyName("managedMemory")] public long ManagedMemory { get; init; }
}
