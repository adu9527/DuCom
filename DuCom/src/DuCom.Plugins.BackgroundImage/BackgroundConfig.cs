using System.Text.Json;
using System.Text.Json.Serialization;

namespace DuCom.Plugins.BackgroundImage;

public sealed record BackgroundConfig
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;

    [JsonPropertyName("imagePath")] public string ImagePath { get; set; } = string.Empty;

    [JsonPropertyName("folderPath")] public string FolderPath { get; set; } = string.Empty;

    [JsonPropertyName("playback")] public string Playback { get; set; } = "single";

    [JsonPropertyName("intervalSeconds")] public int IntervalSeconds { get; set; } = 300;

    [JsonPropertyName("opacity")] public double Opacity { get; set; } = 0.18d;

    [JsonPropertyName("migratedFromHost")] public bool? MigratedFromHost { get; set; }

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };
}
