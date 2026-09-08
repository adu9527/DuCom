using System.Text.Json;

namespace DuCom.Plugin;

public static class PluginManifestValidator
{
    public const string IdPattern = @"^[a-z][a-z0-9-]*(\.[a-z][a-z0-9-]*)+$";
    private static readonly System.Text.RegularExpressions.Regex IdRegex = new(IdPattern, System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex SemVerRegex = new(@"^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex ProtocolVersionRegex = new(@"^(\d+)\.(\d+)$", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex CultureRegex = new(@"^[a-zA-Z]{2,8}(-[a-zA-Z0-9]{1,8})*$", System.Text.RegularExpressions.RegexOptions.Compiled);

    public static bool TryParseStrict(string json, out PluginManifest? manifest, out string? error)
    {
        manifest = null;
        error = null;
        if (string.IsNullOrEmpty(json))
        {
            error = "Manifest is empty.";
            return false;
        }

        if (json.Length > PluginManifest.MaximumManifestJsonBytes)
        {
            error = $"Manifest exceeds {PluginManifest.MaximumManifestJsonBytes} bytes.";
            return false;
        }

        byte[] bytes;
        try
        {
            bytes = System.Text.Encoding.UTF8.GetBytes(json);
        }
        catch (Exception)
        {
            error = "Manifest is not valid UTF-8.";
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(bytes);
            if (!TryFindDuplicateProperties(document.RootElement, "", out string? duplicate))
            {
                error = $"Duplicate JSON property '{duplicate}'.";
                return false;
            }

            manifest = JsonSerializer.Deserialize<PluginManifest>(bytes, JsonOptions);
            if (manifest is null)
            {
                error = "Manifest deserialized to null.";
                return false;
            }

            return true;
        }
        catch (JsonException exception)
        {
            error = $"Manifest is not valid JSON: {exception.Message}";
            return false;
        }
    }

    public static bool Validate(PluginManifest manifest, Func<string, bool>? isOfficialNamespace, out IReadOnlyList<string> errors)
    {
        List<string> found = [];
        void Fail(string message) => found.Add(message);

        if (manifest.ManifestVersion != PluginManifest.CurrentManifestVersion)
        {
            Fail($"manifestVersion must be {PluginManifest.CurrentManifestVersion}.");
        }

        if (string.IsNullOrWhiteSpace(manifest.Id) || manifest.Id.Length > 100 || !IdRegex.IsMatch(manifest.Id))
        {
            Fail($"id '{manifest.Id}' does not match {IdPattern}.");
        }
        else if (manifest.Id.StartsWith("com.ducom.", StringComparison.Ordinal) && isOfficialNamespace?.Invoke(manifest.Id) == false)
        {
            Fail("id uses the reserved com.ducom.* namespace.");
        }

        if (string.IsNullOrWhiteSpace(manifest.Name) || manifest.Name.Length > 120)
        {
            Fail("name is required and must be at most 120 characters.");
        }

        if (string.IsNullOrWhiteSpace(manifest.Version) || !SemVerRegex.IsMatch(manifest.Version))
        {
            Fail($"version '{manifest.Version}' is not a valid SemVer.");
        }

        if (string.IsNullOrWhiteSpace(manifest.ProtocolVersion) || !ProtocolVersionRegex.IsMatch(manifest.ProtocolVersion))
        {
            Fail($"protocolVersion '{manifest.ProtocolVersion}' must be major.minor.");
        }

        if (string.IsNullOrWhiteSpace(manifest.MinHostVersion) || !SemVerRegex.IsMatch(manifest.MinHostVersion))
        {
            Fail($"minHostVersion '{manifest.MinHostVersion}' is not a valid SemVer.");
        }

        if (string.IsNullOrWhiteSpace(manifest.EntryAssembly)
            || manifest.EntryAssembly.Length > 200
            || manifest.EntryAssembly.Contains('/') || manifest.EntryAssembly.Contains('\\')
            || manifest.EntryAssembly.Contains(':', StringComparison.Ordinal)
            || !manifest.EntryAssembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            Fail($"entryAssembly '{manifest.EntryAssembly}' must be a plain DLL filename at the package root.");
        }

        if (string.IsNullOrWhiteSpace(manifest.EntryType) || manifest.EntryType.Length > 400)
        {
            Fail("entryType is required and must be at most 400 characters.");
        }

        if (string.IsNullOrWhiteSpace(manifest.Runtime.Framework) || manifest.Runtime.Framework.Length > 40)
        {
            Fail("runtime.framework is required.");
        }

        if (string.IsNullOrWhiteSpace(manifest.Runtime.Rid) || manifest.Runtime.Rid.Length > 40)
        {
            Fail("runtime.rid is required.");
        }

        if (manifest.Capabilities.Count == 0)
        {
            Fail("capabilities must declare at least one capability.");
        }

        if (manifest.Capabilities.Count > PluginManifest.KnownCapabilities.Count
            || manifest.Capabilities.Distinct(StringComparer.Ordinal).Count() != manifest.Capabilities.Count)
        {
            Fail("capabilities contains duplicates.");
        }

        foreach (string capability in manifest.Capabilities)
        {
            if (!PluginManifest.KnownCapabilities.Contains(capability))
            {
                Fail($"Unknown capability '{capability}'.");
            }
        }

        if (manifest.Permissions.Distinct(StringComparer.Ordinal).Count() != manifest.Permissions.Count)
        {
            Fail("permissions contains duplicates.");
        }

        foreach (string permission in manifest.Permissions)
        {
            if (!PluginManifest.KnownPermissions.Contains(permission))
            {
                Fail($"Unknown permission '{permission}'.");
            }
        }

        if (manifest.DefaultCulture is { } culture
            && (culture.Length > 20 || !CultureRegex.IsMatch(culture)))
        {
            Fail($"defaultCulture '{culture}' is not a valid culture name.");
        }

        foreach ((string? value, string field) in new[]
        {
            (manifest.Description, "description"), (manifest.Author, "author"), (manifest.Homepage, "homepage"),
        })
        {
            if (value is not null && value.Length > 400)
            {
                Fail($"{field} must be at most 400 characters.");
            }
        }

        errors = found;
        return found.Count == 0;
    }

    public static (int Major, int Minor)? ParseProtocolVersion(string value)
    {
        System.Text.RegularExpressions.Match match = ProtocolVersionRegex.Match(value);
        if (!match.Success)
        {
            return null;
        }

        return (int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture));
    }

    public static bool TryCompareSemVer(string left, string right, out int comparison)
    {
        comparison = 0;
        if (TryParseSemVer(left, out Version? leftVersion) && TryParseSemVer(right, out Version? rightVersion)
            && leftVersion is not null && rightVersion is not null)
        {
            comparison = leftVersion.CompareTo(rightVersion);
            return true;
        }

        return false;
    }

    public static bool TryParseSemVer(string value, out Version? version) => Version.TryParse(value.Split(['-', '+'])[0], out version);

    internal static bool TryFindDuplicateProperties(JsonElement element, string path, out string? duplicate)
    {
        duplicate = null;
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                HashSet<string> seen = new(StringComparer.Ordinal);
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if (!seen.Add(property.Name))
                    {
                        duplicate = string.IsNullOrEmpty(path) ? property.Name : $"{path}.{property.Name}";
                        return false;
                    }

                    if (!TryFindDuplicateProperties(property.Value, string.IsNullOrEmpty(path) ? property.Name : $"{path}.{property.Name}", out duplicate))
                    {
                        return false;
                    }
                }

                break;
            case JsonValueKind.Array:
                int index = 0;
                foreach (JsonElement item in element.EnumerateArray())
                {
                    if (!TryFindDuplicateProperties(item, $"{path}[{index}]", out duplicate))
                    {
                        return false;
                    }

                    index++;
                }

                break;
        }

        return true;
    }

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
    };
}
