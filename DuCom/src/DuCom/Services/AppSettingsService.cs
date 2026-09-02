using System.IO;
using System.Text.Json;
using DuCom.Core.Persistence;

namespace DuCom.Services;

public sealed class AppSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public static string SettingsFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DuCom",
        "settings.json");

    public static T? Load<T>() where T : class
    {
        string path = SettingsFilePath;
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (Exception exception)
        {
            Program.DiagnosticLog?.Warning($"Failed to load settings from {path}. {exception.Message}");
            return null;
        }
    }

    public static void Save<T>(T value) where T : class
    {
        string path = SettingsFilePath;
        AtomicFileStore.CommitAll([
            new AtomicFileWrite(path, AtomicFileStore.EncodeUtf8(Serialize(value))),
        ]);
    }

    /// <summary>Serializes with the same options <see cref="Save{T}"/> writes, for staged commits.</summary>
    public static string Serialize<T>(T value) where T : class =>
        JsonSerializer.Serialize(value, JsonOptions);
}
