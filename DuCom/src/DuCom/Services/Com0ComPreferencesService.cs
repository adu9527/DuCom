using System.IO;
using System.Text.Json;
using DuCom.Core.Persistence;

namespace DuCom.Services;

public static class Com0ComPreferencesService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DuCom",
        "com0com-preferences.json");

    public static string LoadSetupcPath()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return string.Empty;
            }

            return JsonSerializer.Deserialize<Com0ComPreferences>(File.ReadAllText(FilePath))?.SetupcPath ?? string.Empty;
        }
        catch (Exception exception)
        {
            Program.DiagnosticLog?.Warning($"Failed to load com0com preferences. {exception.Message}");
            return string.Empty;
        }
    }

    public static void SaveSetupcPath(string setupcPath)
    {
        AtomicFileStore.WriteAllText(FilePath, JsonSerializer.Serialize(new Com0ComPreferences(setupcPath), JsonOptions));
    }

    private sealed record Com0ComPreferences(string SetupcPath);
}
