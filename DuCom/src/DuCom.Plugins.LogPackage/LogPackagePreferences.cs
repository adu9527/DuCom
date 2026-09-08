using System.Text.Json;
using System.Text.Json.Serialization;

namespace DuCom.Plugins.LogPackage;

public sealed record LogPackagePreferences
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("projectName")] public string ProjectName { get; set; } = string.Empty;

    [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;

    [JsonPropertyName("tester")] public string Tester { get; set; } = string.Empty;

    [JsonPropertyName("deviceSoftwareVersion")] public string DeviceSoftwareVersion { get; set; } = string.Empty;

    [JsonPropertyName("reproductionProbability")] public string ReproductionProbability { get; set; } = string.Empty;

    [JsonPropertyName("reproductionTime")] public string ReproductionTime { get; set; } = string.Empty;

    [JsonPropertyName("problemDescription")] public string ProblemDescription { get; set; } = string.Empty;

    [JsonPropertyName("reproductionSteps")] public string ReproductionSteps { get; set; } = string.Empty;

    [JsonPropertyName("notes")] public string Notes { get; set; } = string.Empty;

    [JsonPropertyName("portDevices")] public Dictionary<string, string> PortDevices { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("outputDirectory")] public string OutputDirectory { get; set; } = string.Empty;

    [JsonPropertyName("followLogDirectory")] public bool FollowLogDirectory { get; set; } = true;

    [JsonPropertyName("selectedSessionId")] public string SelectedSessionId { get; set; } = string.Empty;

    [JsonPropertyName("sessionSelection")] public Dictionary<string, bool>? SessionSelection { get; set; }

    [JsonPropertyName("migratedFromHost")] public bool? MigratedFromHost { get; set; }

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };
}
