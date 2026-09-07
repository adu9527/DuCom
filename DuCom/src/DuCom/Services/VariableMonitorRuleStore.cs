using System.IO;
using System.Text.Json;
using DuCom.Core.Diagnostics;
using DuCom.Core.Persistence;

namespace DuCom.Services;

/// <summary>JSON persistence for variable-monitor rules (monitor-rules.json).</summary>
public static class VariableMonitorRuleStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DuCom",
        "monitor-rules.json");

    public static IReadOnlyList<VariableMonitorRule> Load()
    {
        if (!File.Exists(FilePath))
        {
            return [];
        }

        try
        {
            string json = File.ReadAllText(FilePath);
            if (TolerantJsonLoader.TryLoadList<VariableMonitorRule>(json, JsonOptions, out List<VariableMonitorRule> rules, out IReadOnlyList<int> skipped))
            {
                if (skipped.Count > 0)
                {
                    Program.DiagnosticLog?.Warning($"Skipped {skipped.Count} unreadable monitor rule(s) at index {string.Join(", ", skipped)}.");
                }

                return rules;
            }

            Program.DiagnosticLog?.Warning($"Failed to load monitor rules from {FilePath}.");
            return [];
        }
        catch (Exception exception)
        {
            Program.DiagnosticLog?.Warning($"Failed to load monitor rules from {FilePath}.", exception);
            return [];
        }
    }

    public static void Save(IReadOnlyList<VariableMonitorRule> rules)
    {
        try
        {
            AtomicFileStore.WriteAllText(FilePath, JsonSerializer.Serialize(rules, JsonOptions));
        }
        catch (Exception exception)
        {
            Program.DiagnosticLog?.Warning($"Failed to save monitor rules to {FilePath}.", exception);
        }
    }
}
