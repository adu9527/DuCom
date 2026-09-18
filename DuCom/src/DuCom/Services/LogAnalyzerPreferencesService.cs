using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using DuCom.Core.Persistence;

namespace DuCom.Services;

public sealed record LogAnalyzerSourcePreference(
    bool IsSelected = true,
    string Role = "",
    double OffsetMilliseconds = 0);

public sealed record LogAnalyzerColumnVisibility(
    bool Source = true,
    bool Time = true,
    bool Message = true,
    bool Role = true,
    bool Level = true,
    bool Module = true,
    bool Keywords = true,
    bool Comment = true);

public sealed record LogAnalyzerPreferences(
    double Left = double.NaN,
    double Top = double.NaN,
    double Width = 1360,
    double Height = 820,
    bool RealtimeMode = false,
    string NavigationMode = "Keywords",
    bool FollowLatest = true,
    bool SourceCalibrationExpanded = false,
    IReadOnlyDictionary<string, LogAnalyzerSourcePreference>? Sources = null,
    LogAnalyzerColumnVisibility? Columns = null);

public sealed class LogAnalyzerPreferencesService
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };
    private readonly object _gate = new();
    private readonly string _path;

    public LogAnalyzerPreferencesService(string? path = null) => _path = path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DuCom", "log-analyzer-preferences.json");

    public LogAnalyzerPreferences Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_path)) return new();
                return JsonSerializer.Deserialize<LogAnalyzerPreferences>(File.ReadAllText(_path), Options) ?? new();
            }
            catch (Exception exception)
            {
                Program.DiagnosticLog?.Warning("Failed to load log analyzer preferences.", exception);
                return new();
            }
        }
    }

    public void Save(LogAnalyzerPreferences preferences)
    {
        lock (_gate)
        {
            try { AtomicFileStore.WriteAllText(_path, JsonSerializer.Serialize(preferences, Options)); }
            catch (Exception exception) { Program.DiagnosticLog?.Warning("Failed to save log analyzer preferences.", exception); }
        }
    }
}
