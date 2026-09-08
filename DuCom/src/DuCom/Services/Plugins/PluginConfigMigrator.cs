using System.IO;
using System.Reflection;
using System.Text.Json;
using DuCom.Core.Persistence;
using DuCom.PluginHost;

namespace DuCom.Services.Plugins;

/// <summary>
/// One-time migration of the legacy built-in feature settings into the corresponding plugin
/// storages. Originals are preserved untouched; the migrated plugin config records
/// migratedFromHost so a later run never overwrites user changes made after migration.
/// </summary>
public sealed class PluginConfigMigrator
{
    private readonly string _storageRoot;
    private readonly RememberedGrantsStore _rememberedGrants;

    public PluginConfigMigrator(string storageRoot, RememberedGrantsStore rememberedGrants)
    {
        ArgumentException.ThrowIfNullOrEmpty(storageRoot);
        _storageRoot = storageRoot;
        _rememberedGrants = rememberedGrants;
    }

    public bool MigrateLegacyBuiltIns(string legacySettingsJson, string? legacyLogPackagePreferencesJson)
    {
        bool migrated = false;
        migrated |= MigrateBackground(legacySettingsJson);
        migrated |= MigrateLogPackage(legacySettingsJson, legacyLogPackagePreferencesJson);
        return migrated;
    }

    private bool MigrateBackground(string legacySettingsJson)
    {
        try
        {
            JsonDocument document = JsonDocument.Parse(legacySettingsJson, new JsonDocumentOptions { AllowTrailingCommas = true });
            JsonElement root = document.RootElement;
        }
        catch (Exception)
        {
            return false;
        }

        try
        {
            JsonDocument document = JsonDocument.Parse(legacySettingsJson, new JsonDocumentOptions { AllowTrailingCommas = true });
            if (!document.RootElement.TryGetProperty("BackgroundImageEnabled", out JsonElement _))
            {
                return false;
            }

            string target = Path.Combine(_storageRoot, "com.ducom.background-image", "config.json");
            if (File.Exists(target))
            {
                return false;
            }

            JsonElement root = document.RootElement;
            string playback = root.TryGetProperty("BackgroundImagePlaybackMode", out JsonElement modeElement) && modeElement.ValueKind == JsonValueKind.Number
                ? modeElement.GetInt32() switch { 1 => "sequential", 2 => "random", _ => "single" }
                : "single";
            string imagePath = GetString(root, "BackgroundImagePath");
            string folderPath = GetString(root, "BackgroundImageFolderPath");
            string json = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["schemaVersion"] = 1,
                ["enabled"] = root.TryGetProperty("BackgroundImageEnabled", out JsonElement enabled) && enabled.ValueKind == JsonValueKind.True,
                ["imagePath"] = imagePath,
                ["folderPath"] = folderPath,
                ["playback"] = playback,
                ["intervalSeconds"] = root.TryGetProperty("BackgroundImageIntervalSeconds", out JsonElement interval) && interval.ValueKind == JsonValueKind.Number ? interval.GetInt32() : 300,
                ["opacity"] = root.TryGetProperty("BackgroundImageOpacity", out JsonElement opacity) && opacity.ValueKind == JsonValueKind.Number ? opacity.GetDouble() : 0.18d,
                ["migratedFromHost"] = true,
            }, new JsonSerializerOptions { WriteIndented = true });

            AtomicFileStore.WriteAllText(target, json);
            if (!string.IsNullOrEmpty(imagePath))
            {
                _rememberedGrants.Add("com.ducom.background-image", imagePath);
            }

            if (!string.IsNullOrEmpty(folderPath))
            {
                _rememberedGrants.Add("com.ducom.background-image", folderPath);
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private bool MigrateLogPackage(string legacySettingsJson, string? legacyJson)
    {
        if (string.IsNullOrEmpty(legacyJson))
        {
            return false;
        }

        string target = Path.Combine(_storageRoot, "com.ducom.log-package", "config.json");
        if (File.Exists(target))
        {
            return false;
        }

        try
        {
            JsonDocument document = JsonDocument.Parse(legacyJson, new JsonDocumentOptions { AllowTrailingCommas = true });
            JsonElement root = document.RootElement;
            using JsonDocument settingsDocument = JsonDocument.Parse(legacySettingsJson, new JsonDocumentOptions { AllowTrailingCommas = true });
            JsonElement settings = settingsDocument.RootElement;
            JsonElement deviceNames = root.TryGetProperty("PortDevices", out JsonElement devices) && devices.ValueKind == JsonValueKind.Object
                ? devices
                : root.TryGetProperty("DeviceNames", out JsonElement names) && names.ValueKind == JsonValueKind.Object ? names : default;
            string outputDirectory = ResolveLegacyOutputDirectory(root, settings);
            Dictionary<string, object?> payload = new()
            {
                ["schemaVersion"] = 1,
                ["projectName"] = FirstString(root, "ProjectName", "Title"),
                ["title"] = FirstString(root, "Title", "ProjectName"),
                ["tester"] = GetString(root, "Tester"),
                ["deviceSoftwareVersion"] = GetString(root, "DeviceSoftwareVersion"),
                ["reproductionProbability"] = GetString(root, "ReproductionProbability"),
                ["reproductionTime"] = GetString(root, "ReproductionTime"),
                ["problemDescription"] = GetString(root, "ProblemDescription"),
                ["reproductionSteps"] = GetString(root, "ReproductionSteps"),
                ["notes"] = GetString(root, "Notes"),
                ["portDevices"] = deviceNames.ValueKind == JsonValueKind.Object
                    ? deviceNames.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.GetString() ?? string.Empty)
                    : new Dictionary<string, string>(),
                ["outputDirectory"] = outputDirectory,
                // Legacy semantics: an empty output directory means "follow the log directory".
                ["followLogDirectory"] = string.IsNullOrEmpty(outputDirectory),
                ["migratedFromHost"] = true,
            };
            AtomicFileStore.WriteAllText(target, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
            if (!string.IsNullOrEmpty(outputDirectory))
            {
                _rememberedGrants.Add("com.ducom.log-package", outputDirectory);
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private string ResolveLegacyOutputDirectory(JsonElement preferences, JsonElement settings) =>
        FirstString(preferences, "OutputDirectory", "LogOutputDirectory") is { Length: > 0 } fromPreferences
            ? fromPreferences
            : FirstString(settings, "LogPackageOutputDirectory", "OutputDirectory");

    private static string GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

    private static string FirstString(JsonElement element, params string[] properties) => properties.Select(property => GetString(element, property)).FirstOrDefault(value => value.Length > 0) ?? string.Empty;
}
