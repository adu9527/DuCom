using System.Text.Json;
using System.Text.Json.Serialization;

namespace DuCom.Core.Protocols.Custom;

public sealed record CustomBinaryProfileLoadResult(
    int SchemaVersion,
    IReadOnlyList<CustomBinaryProfile> Profiles,
    IReadOnlyList<ProtocolDiagnostic> Diagnostics);

public static class CustomBinaryProfileJson
{
    public static CustomBinaryProfileLoadResult Load(string json, CustomBinaryProfileLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(json);
        limits ??= new CustomBinaryProfileLimits();
        List<ProtocolDiagnostic> diagnostics = [];
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = limits.MaximumNestingDepth + 8 });
        }
        catch (JsonException exception)
        {
            diagnostics.Add(new ProtocolDiagnostic("custom.json.invalid", exception.Message, ProtocolDiagnosticSeverity.Error, exception.Path ?? "$"));
            return new CustomBinaryProfileLoadResult(0, [], diagnostics.AsReadOnly());
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                diagnostics.Add(new ProtocolDiagnostic("custom.json.root", "Configuration root must be an object.", ProtocolDiagnosticSeverity.Error, "$"));
                return new CustomBinaryProfileLoadResult(0, [], diagnostics.AsReadOnly());
            }

            int schemaVersion = document.RootElement.TryGetProperty("schemaVersion", out JsonElement versionElement) && versionElement.TryGetInt32(out int parsedVersion)
                ? parsedVersion
                : 0;
            if (schemaVersion != 1)
            {
                diagnostics.Add(new ProtocolDiagnostic("custom.json.schema", "schemaVersion must be 1.", ProtocolDiagnosticSeverity.Error, "$.schemaVersion"));
            }

            if (!document.RootElement.TryGetProperty("profiles", out JsonElement profilesElement) || profilesElement.ValueKind != JsonValueKind.Array)
            {
                diagnostics.Add(new ProtocolDiagnostic("custom.json.profiles", "profiles must be an array.", ProtocolDiagnosticSeverity.Error, "$.profiles"));
                return new CustomBinaryProfileLoadResult(schemaVersion, [], diagnostics.AsReadOnly());
            }

            JsonSerializerOptions options = new(JsonSerializerDefaults.Web)
            {
                PropertyNameCaseInsensitive = true,
                MaxDepth = limits.MaximumNestingDepth + 8,
            };
            options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
            List<CustomBinaryProfile> profiles = [];
            HashSet<string> ids = new(StringComparer.OrdinalIgnoreCase);
            int index = 0;
            foreach (JsonElement element in profilesElement.EnumerateArray())
            {
                string path = $"$.profiles[{index}]";
                try
                {
                    CustomBinaryProfile? profile = element.Deserialize<CustomBinaryProfile>(options);
                    if (profile is null)
                    {
                        diagnostics.Add(new ProtocolDiagnostic("custom.json.profile", "Profile cannot be null.", ProtocolDiagnosticSeverity.Error, path));
                    }
                    else
                    {
                        CustomBinaryProfileValidationResult validation = CustomBinaryProfileValidator.Validate(profile, limits, path);
                        diagnostics.AddRange(validation.Diagnostics);
                        if (!ids.Add(profile.Id))
                        {
                            diagnostics.Add(new ProtocolDiagnostic("custom.profile.duplicate-id", "Profile ID must be unique.", ProtocolDiagnosticSeverity.Error, path + ".id"));
                        }
                        else if (validation.IsValid)
                        {
                            profiles.Add(profile);
                        }
                    }
                }
                catch (JsonException exception)
                {
                    diagnostics.Add(new ProtocolDiagnostic("custom.json.profile", exception.Message, ProtocolDiagnosticSeverity.Error, CombinePath(path, exception.Path)));
                }

                index++;
            }

            return new CustomBinaryProfileLoadResult(schemaVersion, profiles.AsReadOnly(), diagnostics.AsReadOnly());
        }
    }

    private static string CombinePath(string parent, string? child) => string.IsNullOrEmpty(child) || child == "$"
        ? parent
        : parent + child[1..];
}

public sealed class HexByteArrayJsonConverter : JsonConverter<byte[]>
{
    public override byte[] Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            string text = reader.GetString() ?? string.Empty;
            string compact = string.Concat(text.Where(character => !char.IsWhiteSpace(character) && character is not '-' and not ':'));
            if (compact.Length % 2 != 0)
            {
                throw new JsonException("Hex byte strings must contain complete byte pairs.");
            }

            try
            {
                return Convert.FromHexString(compact);
            }
            catch (FormatException exception)
            {
                throw new JsonException("Hex byte string contains an invalid character.", exception);
            }
        }

        if (reader.TokenType == JsonTokenType.StartArray)
        {
            List<byte> bytes = [];
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (!reader.TryGetByte(out byte value)) throw new JsonException("Byte array values must be between 0 and 255.");
                bytes.Add(value);
            }

            return bytes.ToArray();
        }

        throw new JsonException("Expected a hexadecimal string or byte array.");
    }

    public override void Write(Utf8JsonWriter writer, byte[] value, JsonSerializerOptions options) =>
        writer.WriteStringValue(string.Join(' ', value.Select(item => item.ToString("X2", System.Globalization.CultureInfo.InvariantCulture))));
}
