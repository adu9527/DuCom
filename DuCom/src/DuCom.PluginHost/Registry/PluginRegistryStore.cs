using System.Text.Json.Serialization;
using System.Security.Cryptography;
using DuCom.Core.Persistence;
using DuCom.Plugin;

namespace DuCom.PluginHost.Registry;

public sealed record InstalledVersionRecord
{
    [JsonPropertyName("version")] public string Version { get; init; } = string.Empty;
    [JsonPropertyName("path")] public string Path { get; init; } = string.Empty;
    [JsonPropertyName("digest")] public string Digest { get; init; } = string.Empty;
    [JsonPropertyName("installedAtUtc")] public DateTime InstalledAtUtc { get; init; }
    [JsonPropertyName("source")] public string Source { get; init; } = "dcpack";

    public const string SourceBuiltIn = "builtin";
    public const string SourceDcPack = "dcpack";
}

public sealed record FaultDisableRecord
{
    [JsonPropertyName("version")] public string Version { get; init; } = string.Empty;
    [JsonPropertyName("digest")] public string Digest { get; init; } = string.Empty;
    [JsonPropertyName("reason")] public string Reason { get; init; } = string.Empty;
    [JsonPropertyName("activationId")] public string ActivationId { get; init; } = string.Empty;
    [JsonPropertyName("faultAtUtc")] public DateTime FaultAtUtc { get; init; }
    [JsonPropertyName("notified")] public bool Notified { get; init; }
    [JsonPropertyName("exitConfirmed")] public bool ExitConfirmed { get; init; }
}

public sealed record PendingUpdateRecord
{
    [JsonPropertyName("version")] public string Version { get; init; } = string.Empty;
    [JsonPropertyName("digest")] public string Digest { get; init; } = string.Empty;
    [JsonPropertyName("previousVersion")] public string PreviousVersion { get; init; } = string.Empty;
    [JsonPropertyName("requiresPermissionConsent")] public bool RequiresPermissionConsent { get; init; }
    [JsonPropertyName("newPermissions")] public IReadOnlyList<string> NewPermissions { get; init; } = [];
}

public sealed record RememberedGrantRecord
{
    [JsonPropertyName("kind")] public string Kind { get; init; } = "read";
    [JsonPropertyName("path")] public string Path { get; init; } = string.Empty;
    [JsonPropertyName("grantedAtUtc")] public DateTime GrantedAtUtc { get; init; }
}

public sealed record AttemptRecord
{
    [JsonPropertyName("activationId")] public string ActivationId { get; init; } = string.Empty;
    [JsonPropertyName("version")] public string Version { get; init; } = string.Empty;
    [JsonPropertyName("digest")] public string Digest { get; init; } = string.Empty;
    [JsonPropertyName("hostRunId")] public string HostRunId { get; init; } = string.Empty;
    [JsonPropertyName("startedAtUtc")] public DateTime StartedUtc { get; init; }
    [JsonPropertyName("endedCleanly")] public bool? EndedCleanly { get; init; }
    [JsonPropertyName("endedAtUtc")] public DateTime? EndedUtc { get; init; }
    [JsonPropertyName("recoveryNotified")] public bool RecoveryNotified { get; init; }
}

public sealed record PluginRegistryEntry
{
    [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;
    [JsonPropertyName("installedVersions")] public List<InstalledVersionRecord> InstalledVersions { get; init; } = [];
    [JsonPropertyName("selectedVersion")] public string SelectedVersion { get; init; } = string.Empty;
    [JsonPropertyName("enabled")] public bool Enabled { get; init; } = true;
    [JsonPropertyName("builtIn")] public bool BuiltIn { get; init; }
    [JsonPropertyName("faultDisabled")] public FaultDisableRecord? FaultDisabled { get; init; }
    [JsonPropertyName("pendingUpdate")] public PendingUpdateRecord? PendingUpdate { get; init; }
    [JsonPropertyName("approvedPermissions")] public List<string> ApprovedPermissions { get; init; } = [];
    [JsonPropertyName("rememberedGrants")] public List<RememberedGrantRecord> RememberedGrants { get; init; } = [];
    [JsonPropertyName("lastAttempt")] public AttemptRecord? LastAttempt { get; init; }
}

public sealed record PendingNoticeRecord
{
    [JsonPropertyName("id")] public string Id { get; init; } = Guid.NewGuid().ToString("N");
    [JsonPropertyName("pluginId")] public string PluginId { get; init; } = string.Empty;
    [JsonPropertyName("version")] public string Version { get; init; } = string.Empty;
    [JsonPropertyName("reason")] public string Reason { get; init; } = string.Empty;
    [JsonPropertyName("activationId")] public string ActivationId { get; init; } = string.Empty;
    [JsonPropertyName("exitConfirmed")] public bool ExitConfirmed { get; init; }
    [JsonPropertyName("kind")] public string Kind { get; init; } = NoticeKinds.Fault;
    [JsonPropertyName("createdAtUtc")] public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;
    [JsonPropertyName("consumed")] public bool Consumed { get; init; }
}

public static class NoticeKinds
{
    public const string Fault = "fault";
    public const string Recovery = "recovery";
}

public sealed record PluginRegistryData
{
    public const int CurrentSchemaVersion = 1;

    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    [JsonPropertyName("plugins")] public Dictionary<string, PluginRegistryEntry> Plugins { get; init; } = new(StringComparer.Ordinal);
    [JsonPropertyName("safeStartAllPlugins")] public bool SafeStartAllPlugins { get; init; }
    [JsonPropertyName("pendingNotices")] public List<PendingNoticeRecord> PendingNotices { get; init; } = [];
    [JsonPropertyName("currentHostRunId")] public string CurrentHostRunId { get; init; } = string.Empty;
}

public sealed class PluginRegistryStore
{
    private readonly string _path;
    private readonly object _gate = new();
    private PluginRegistryData _data = new();

    public PluginRegistryStore(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        _path = path;
    }

    public string HostRunId { get; } = Guid.NewGuid().ToString("N");

    public PluginRegistryData Current
    {
        get
        {
            lock (_gate)
            {
                return _data;
            }
        }
    }

    public void Load()
    {
        lock (_gate)
        {
            PluginRegistryData? loaded = null;
            bool exists = File.Exists(_path);
            if (exists)
            {
                try
                {
                    string json = File.ReadAllText(_path);
                    loaded = System.Text.Json.JsonSerializer.Deserialize<PluginRegistryData>(json, PluginWire.JsonOptions);
                    if (loaded is null || loaded.SchemaVersion != PluginRegistryData.CurrentSchemaVersion
                        || loaded.Plugins is null || loaded.PendingNotices is null
                        || loaded.Plugins.Any(pair => pair.Value is null || pair.Key != pair.Value.Id
                            || pair.Value.SelectedVersion is null || pair.Value.InstalledVersions is null
                            || pair.Value.InstalledVersions.Any(version => version is null)
                            || pair.Value.ApprovedPermissions is null || pair.Value.RememberedGrants is null)
                        || loaded.PendingNotices.Any(notice => notice is null))
                    {
                        throw new System.Text.Json.JsonException("Invalid plugin registry structure.");
                    }
                }
                catch (Exception)
                {
                    loaded = null;
                    try
                    {
                        File.Copy(_path, _path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"), overwrite: true);
                    }
                    catch (Exception)
                    {
                    }
                }
            }

            _data = loaded is { SchemaVersion: PluginRegistryData.CurrentSchemaVersion }
                ? loaded with { CurrentHostRunId = HostRunId }
                : new PluginRegistryData { CurrentHostRunId = HostRunId, SafeStartAllPlugins = exists };
        }
    }

    public T Mutate<T>(Func<PluginRegistryData, (PluginRegistryData Next, T Result)> mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        lock (_gate)
        {
            (PluginRegistryData next, T result) = mutation(CloneLocked(_data));
            SaveLocked(next);
            _data = next;
            return result;
        }
    }

    public void Mutate(Func<PluginRegistryData, PluginRegistryData> mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        lock (_gate)
        {
            PluginRegistryData next = mutation(CloneLocked(_data));
            SaveLocked(next);
            _data = next;
        }
    }

    public void Mutate(Action<PluginRegistryData> mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        lock (_gate)
        {
            PluginRegistryData next = CloneLocked(_data);
            mutation(next);
            SaveLocked(next);
            _data = next;
        }
    }

    private void SaveLocked(PluginRegistryData data)
    {
        string json = System.Text.Json.JsonSerializer.Serialize(
            data with { CurrentHostRunId = HostRunId },
            new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            });
        AtomicFileStore.WriteAllText(_path, json);
    }

    private static PluginRegistryData CloneLocked(PluginRegistryData source)
    {
        string json = System.Text.Json.JsonSerializer.Serialize(source, PluginWire.JsonOptions);
        return System.Text.Json.JsonSerializer.Deserialize<PluginRegistryData>(json, PluginWire.JsonOptions)
            ?? throw new System.Text.Json.JsonException("Cannot clone plugin registry data.");
    }
}



