using System.Text.Json.Serialization;

namespace DuCom.Plugin;

public sealed record PluginLimits
{
    [JsonPropertyName("startupTimeoutMs")] public int StartupTimeoutMs { get; init; } = 10_000;
    [JsonPropertyName("activationTimeoutMs")] public int ActivationTimeoutMs { get; init; } = 10_000;
    [JsonPropertyName("callTimeoutMs")] public int CallTimeoutMs { get; init; } = 2_000;
    [JsonPropertyName("stopGraceMs")] public int StopGraceMs { get; init; } = 3_000;
    [JsonPropertyName("heartbeatIntervalMs")] public int HeartbeatIntervalMs { get; init; } = 2_000;
    [JsonPropertyName("heartbeatMissLimit")] public int HeartbeatMissLimit { get; init; } = 3;
    [JsonPropertyName("storageQuotaBytes")] public long StorageQuotaBytes { get; init; } = 4 * 1024 * 1024;
    [JsonPropertyName("tempQuotaBytes")] public long TempQuotaBytes { get; init; } = 512 * 1024 * 1024;
    [JsonPropertyName("readChunkMaxBytes")] public int ReadChunkMaxBytes { get; init; } = 512 * 1024;
    [JsonPropertyName("serialQueueBlocks")] public int SerialQueueBlocks { get; init; } = 256;
    [JsonPropertyName("serialQueueMaxBytes")] public long SerialQueueMaxBytes { get; init; } = 2 * 1024 * 1024;
    [JsonPropertyName("serialSustainedDropWindowMs")] public int SerialSustainedDropWindowMs { get; init; } = 30_000;
    [JsonPropertyName("serialSustainedDropThreshold")] public double SerialSustainedDropThreshold { get; init; } = 0.5;
    [JsonPropertyName("taskDeadlineMs")] public int TaskDeadlineMs { get; init; } = 600_000;
    [JsonPropertyName("taskFirstProgressMs")] public int TaskFirstProgressMs { get; init; } = 5_000;
    [JsonPropertyName("taskProgressIntervalMs")] public int TaskProgressIntervalMs { get; init; } = 10_000;
    [JsonPropertyName("diagMessagesPerMinute")] public int DiagMessagesPerMinute { get; init; } = 60;
    [JsonPropertyName("uiUpdatesPerMinute")] public int UiUpdatesPerMinute { get; init; } = 240;
    [JsonPropertyName("processMemoryLimitBytes")] public long ProcessMemoryLimitBytes { get; init; } = 256 * 1024 * 1024;
}

public sealed record PluginHandshakeInfo
{
    [JsonPropertyName("runnerVersion")] public string RunnerVersion { get; init; } = string.Empty;
    [JsonPropertyName("pluginId")] public string PluginId { get; init; } = string.Empty;
    [JsonPropertyName("pluginVersion")] public string PluginVersion { get; init; } = string.Empty;
    [JsonPropertyName("packageDigest")] public string PackageDigest { get; init; } = string.Empty;
    [JsonPropertyName("protocolVersion")] public string ProtocolVersion { get; init; } = PluginWire.ProtocolVersion;
    [JsonPropertyName("sessionId")] public string SessionId { get; init; } = string.Empty;
    [JsonPropertyName("activationId")] public string ActivationId { get; init; } = string.Empty;
    [JsonPropertyName("hostVersion")] public string HostVersion { get; init; } = string.Empty;
    [JsonPropertyName("culture")] public string Culture { get; init; } = "en-US";
    [JsonPropertyName("grantedCapabilities")] public IReadOnlyList<string> GrantedCapabilities { get; init; } = [];
    [JsonPropertyName("grantedPermissions")] public IReadOnlyList<string> GrantedPermissions { get; init; } = [];
    [JsonPropertyName("limits")] public PluginLimits Limits { get; init; } = new();
}
