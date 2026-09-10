using System.Globalization;
using System.IO;
using System.Text.Json;

namespace DuCom;

public partial class App
{
    private static Dictionary<string, string> ParseArguments(string[] values)
    {
        Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < values.Length; index++)
        {
            if (values[index].StartsWith("--", StringComparison.Ordinal))
            {
                string key = values[index][2..];
                if (index + 1 < values.Length && !values[index + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    result[key] = values[++index];
                }
                else
                {
                    result[key] = "true";
                }
            }
        }

        return result;
    }

    private static string ResolveLanguage(Dictionary<string, string> arguments)
    {
        string requested = arguments.TryGetValue("language", out string? language)
            ? language
            : CultureInfo.CurrentUICulture.Name;
        return string.Equals(requested, "zh-CN", StringComparison.OrdinalIgnoreCase) ? "zh-CN" : "en-US";
    }

    private static string ResolveThemeMode(Dictionary<string, string> arguments, string persistedThemeMode)
    {
        string requested = arguments.TryGetValue("theme", out string? theme) ? theme : persistedThemeMode;
        return requested.Equals("Light", StringComparison.OrdinalIgnoreCase) ? "Light" : "Dark";
    }

    private static string LoadPersistedThemeMode()
    {
        try
        {
            string path = Services.AppSettingsService.SettingsFilePath;
            if (!File.Exists(path))
            {
                return "Dark";
            }

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty("ThemeMode", out JsonElement value)
                ? value.GetString() ?? "Dark"
                : "Dark";
        }
        catch (Exception exception)
        {
            Program.DiagnosticLog?.Warning("Failed to read startup theme from settings.", exception);
            return "Dark";
        }
    }
}
