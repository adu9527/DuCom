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

    public static T? Load<T>() where T : class => LoadWithReport<T>(out _);

    /// <summary>
    /// Loads the settings file with per-field fault isolation: one unreadable property
    /// degrades to that property's default instead of discarding the whole snapshot.
    /// </summary>
    public static T? LoadWithReport<T>(out IReadOnlyList<string> skippedProperties) where T : class
    {
        skippedProperties = [];
        string path = SettingsFilePath;
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            string json = File.ReadAllText(path);
            if (TolerantJsonLoader.TryLoad(json, JsonOptions, out T? value, out IReadOnlyList<string> skipped))
            {
                skippedProperties = skipped;
                return value;
            }

            Program.DiagnosticLog?.Warning($"Failed to load settings from {path}.");
            return null;
        }
        catch (Exception exception)
        {
            Program.DiagnosticLog?.Warning($"Failed to load settings from {path}.", exception);
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
