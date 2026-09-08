using System.Text.Json.Serialization;

namespace DuCom.Plugin;

public sealed record PluginRuntimeInfo
{
    [JsonPropertyName("framework")] public string Framework { get; init; } = string.Empty;
    [JsonPropertyName("rid")] public string Rid { get; init; } = string.Empty;
}

public sealed record PluginManifest
{
    public const int CurrentManifestVersion = 1;
    public const int MaximumManifestJsonBytes = 64 * 1024;

    public static readonly IReadOnlySet<string> KnownCapabilities = new HashSet<string>(StringComparer.Ordinal)
    {
        Capability.Menu,
        Capability.ToolPage,
        Capability.SettingsPanel,
        Capability.BackgroundImage,
    };

    public static readonly IReadOnlySet<string> KnownPermissions = new HashSet<string>(StringComparer.Ordinal)
    {
        Permission.StorageOwn,
        Permission.SerialRead,
        Permission.SerialLogsRead,
        Permission.FilesUserSelectedRead,
        Permission.FilesUserSelectedWrite,
    };

    [JsonPropertyName("manifestVersion")] public int ManifestVersion { get; init; }
    [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
    [JsonPropertyName("version")] public string Version { get; init; } = string.Empty;
    [JsonPropertyName("protocolVersion")] public string ProtocolVersion { get; init; } = string.Empty;
    [JsonPropertyName("minHostVersion")] public string MinHostVersion { get; init; } = string.Empty;
    [JsonPropertyName("entryAssembly")] public string EntryAssembly { get; init; } = string.Empty;
    [JsonPropertyName("entryType")] public string EntryType { get; init; } = string.Empty;
    [JsonPropertyName("runtime")] public PluginRuntimeInfo Runtime { get; init; } = new();
    [JsonPropertyName("capabilities")] public IReadOnlyList<string> Capabilities { get; init; } = [];
    [JsonPropertyName("permissions")] public IReadOnlyList<string> Permissions { get; init; } = [];
    [JsonPropertyName("defaultCulture")] public string? DefaultCulture { get; init; }
    [JsonPropertyName("description")] public string? Description { get; init; }
    [JsonPropertyName("author")] public string? Author { get; init; }
    [JsonPropertyName("homepage")] public string? Homepage { get; init; }
}

public static class Capability
{
    public const string Menu = "menu";
    public const string ToolPage = "tool-page";
    public const string SettingsPanel = "settings-panel";
    public const string BackgroundImage = "background-image";
}

public static class Permission
{
    public const string StorageOwn = "storage.own";
    public const string SerialRead = "serial.read";
    public const string SerialLogsRead = "serial.logs.read";
    public const string FilesUserSelectedRead = "files.user-selected.read";
    public const string FilesUserSelectedWrite = "files.user-selected.write";
}
