using System.IO;
using System.Text.Json;
using DuCom.Core.Diagnostics;
using DuCom.Core.Persistence;

namespace DuCom.Services;

/// <summary>JSON persistence for watchdog rules (watchdog-rules.json).</summary>
public static class WatchdogRuleStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DuCom",
        "watchdog-rules.json");

    public static IReadOnlyList<WatchdogRule> Load()
    {
        if (!File.Exists(FilePath))
        {
            return [];
        }

        try
        {
            string json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<List<WatchdogRule>>(json, JsonOptions) ?? [];
        }
        catch (Exception exception)
        {
            Program.DiagnosticLog?.Warning($"Failed to load watchdog rules from {FilePath}. {exception.Message}");
            return [];
        }
    }

    public static void Save(IReadOnlyList<WatchdogRule> rules)
    {
        try
        {
            AtomicFileStore.WriteAllText(FilePath, JsonSerializer.Serialize(rules, JsonOptions));
        }
        catch (Exception exception)
        {
            Program.DiagnosticLog?.Warning($"Failed to save watchdog rules to {FilePath}. {exception.Message}");
        }
    }
}
