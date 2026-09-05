using System.IO;
using System.Text.Json;
using DuCom.Core.Persistence;

namespace DuCom.Services;

internal sealed record LogPackagePreferences(
    string ProjectName = "",
    string Tester = "",
    Dictionary<string, string>? DeviceNames = null,
    double Left = double.NaN,
    double Top = double.NaN,
    double Width = 1080,
    double Height = 900,
    bool Maximized = false,
    string? Title = null,
    string? DeviceSoftwareVersion = null,
    string? ReproductionProbability = null,
    string? ProblemDescription = null,
    string? ReproductionSteps = null,
    string? Notes = null);

internal static class LogPackagePreferencesService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DuCom", "log-package-preferences.json");

    public static LogPackagePreferences Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<LogPackagePreferences>(File.ReadAllText(FilePath), JsonOptions) ?? new()
                : new();
        }
        catch (Exception exception)
        {
            Program.DiagnosticLog?.Warning($"Failed to load log package preferences. {exception.Message}");
            return new();
        }
    }

    public static void Save(LogPackagePreferences preferences)
    {
        try
        {
            AtomicFileStore.CommitAll([new AtomicFileWrite(FilePath, AtomicFileStore.EncodeUtf8(JsonSerializer.Serialize(preferences, JsonOptions)))]);
        }
        catch (Exception exception)
        {
            Program.DiagnosticLog?.Warning($"Failed to save log package preferences. {exception.Message}");
        }
    }
}
