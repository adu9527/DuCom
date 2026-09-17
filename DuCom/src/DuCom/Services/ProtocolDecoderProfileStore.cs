using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using DuCom.Core.Persistence;
using DuCom.Core.Protocols.Custom;

namespace DuCom.Services;

public sealed record ProtocolDecoderProfile(
    string Id,
    string Name,
    string Type,
    CustomBinaryProfile? Custom = null);

public sealed record ProtocolDecoderConfiguration(
    int SchemaVersion,
    string SelectedProfileId,
    IReadOnlyList<ProtocolDecoderProfile> Profiles,
    IReadOnlyList<string> Diagnostics);

public static class ProtocolDecoderProfileStore
{
    private static readonly JsonSerializerOptions Options = CreateOptions();
    public static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DuCom", "protocol-decoder.json");

    public static ProtocolDecoderConfiguration Load(string? path = null)
    {
        path ??= FilePath;
        if (!File.Exists(path))
        {
            ProtocolDecoderConfiguration defaults = CreateDefaults();
            Save(defaults, path);
            return defaults;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement root = document.RootElement;
            string selected = root.TryGetProperty("selectedProfileId", out JsonElement selectedElement)
                ? selectedElement.GetString() ?? "modbus-rtu-default"
                : "modbus-rtu-default";
            List<ProtocolDecoderProfile> profiles = [];
            List<string> diagnostics = [];
            HashSet<string> ids = new(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("profiles", out JsonElement profileElements) && profileElements.ValueKind == JsonValueKind.Array)
            {
                int index = 0;
                foreach (JsonElement element in profileElements.EnumerateArray())
                {
                    try
                    {
                        ProtocolDecoderProfile? profile = element.Deserialize<ProtocolDecoderProfile>(Options);
                        if (profile is null || string.IsNullOrWhiteSpace(profile.Id) || string.IsNullOrWhiteSpace(profile.Name) ||
                            profile.Type is not ("modbusRtu" or "customBinary") ||
                            profile.Type == "customBinary" && profile.Custom is null)
                        {
                            diagnostics.Add($"profiles[{index}] is invalid and was skipped.");
                        }
                        else if (!ids.Add(profile.Id))
                        {
                            diagnostics.Add($"profiles[{index}] has a duplicate id and was skipped.");
                        }
                        else
                        {
                            profiles.Add(profile);
                        }
                    }
                    catch (Exception exception) when (exception is JsonException or ArgumentException or NotSupportedException)
                    {
                        diagnostics.Add($"profiles[{index}] was skipped: {exception.Message}");
                    }
                    index++;
                }
            }

            if (profiles.Count == 0) return CreateDefaults() with { Diagnostics = diagnostics };
            if (profiles.All(profile => !string.Equals(profile.Id, selected, StringComparison.OrdinalIgnoreCase))) selected = profiles[0].Id;
            return new ProtocolDecoderConfiguration(1, selected, profiles.AsReadOnly(), diagnostics.AsReadOnly());
        }
        catch (Exception exception)
        {
            Program.DiagnosticLog?.Warning("Failed to load protocol decoder profiles.", exception);
            return CreateDefaults() with { Diagnostics = [exception.Message] };
        }
    }

    public static void Save(ProtocolDecoderConfiguration configuration, string? path = null)
    {
        path ??= FilePath;
        try
        {
            AtomicFileStore.WriteAllText(path, JsonSerializer.Serialize(configuration with { SchemaVersion = 1, Diagnostics = [] }, Options));
        }
        catch (Exception exception)
        {
            Program.DiagnosticLog?.Warning("Failed to save protocol decoder profiles.", exception);
        }
    }

    public static void ResetDefaults(string? path = null) => Save(CreateDefaults(), path);

    public static ProtocolDecoderConfiguration CreateDefaults()
    {
        CustomBinaryProfile custom = new()
        {
            Id = "custom-aa55-example",
            Name = "AA55 Length Frame",
            MaximumFrameLength = 1024,
            Sync = new BinarySyncDefinition { Bytes = [0xAA, 0x55] },
            Length = new BinaryLengthDefinition { Offset = 2, Size = 2, ByteOrder = BinaryByteOrder.LittleEndian },
            Fields =
            [
                new BinaryFieldDefinition { Name = "Command", Offset = 4, Type = BinaryFieldType.UInt8 },
                new BinaryFieldDefinition { Name = "Value", Offset = 5, Type = BinaryFieldType.Int16,
                    ByteOrder = BinaryByteOrder.LittleEndian, Scale = 0.1, Unit = "unit" },
            ],
        };
        return new ProtocolDecoderConfiguration(1, "modbus-rtu-default",
            [new("modbus-rtu-default", "Modbus RTU", "modbusRtu"), new(custom.Id, custom.Name, "customBinary", custom)], []);
    }

    private static JsonSerializerOptions CreateOptions()
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web) { WriteIndented = true, PropertyNameCaseInsensitive = true };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
