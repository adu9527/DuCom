using System.IO;
using System.Text.Json;
using DuCom.Core.Persistence;

namespace DuCom.Services.Plugins;

public sealed record TimerShortcutPreferences(
    string ToggleRun = "Space",
    string Lap = "L",
    string Reset = "R",
    string Export = "Ctrl+E");

public sealed record PluginToolWindowPreferences(bool Topmost = false, TimerShortcutPreferences? TimerShortcuts = null);

/// <summary>
/// Preferences shared by every plugin tool window: the always-on-top state, toggled from
/// the window footer. Persisted globally next to the other DuCom window preferences.
/// </summary>
public static class PluginToolWindowPreferencesService
{
    private static readonly object SyncRoot = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DuCom",
        "plugin-tool-window.json");

    public static PluginToolWindowPreferences Load()
    {
        try
        {
            lock (SyncRoot)
            {
                string? json = File.Exists(FilePath) ? File.ReadAllText(FilePath) : null;
                if (string.IsNullOrEmpty(json))
                {
                    return new();
                }

                return TolerantJsonLoader.TryLoad<PluginToolWindowPreferences>(json, JsonOptions, out PluginToolWindowPreferences? preferences, out _) && preferences is not null
                    ? preferences
                    : new();
            }
        }
        catch (Exception exception)
        {
            Program.DiagnosticLog?.Warning($"Failed to load plugin tool window preferences. {exception.Message}");
            return new();
        }
    }

    public static void Save(PluginToolWindowPreferences preferences)
    {
        try
        {
            lock (SyncRoot)
            {
                AtomicFileStore.WriteAllText(FilePath, JsonSerializer.Serialize(preferences, JsonOptions));
            }
        }
        catch (Exception exception)
        {
            Program.DiagnosticLog?.Warning($"Failed to save plugin tool window preferences. {exception.Message}");
        }
    }

    public static void SaveTimerShortcuts(TimerShortcutPreferences shortcuts)
    {
        ArgumentNullException.ThrowIfNull(shortcuts);
        Save(Load() with { TimerShortcuts = shortcuts });
    }

    public static void SaveTimerShortcut(string commandId, string gesture)
    {
        TimerShortcutPreferences shortcuts = Load().TimerShortcuts ?? new();
        shortcuts = commandId switch
        {
            "toggle-run" => shortcuts with { ToggleRun = gesture },
            "lap" => shortcuts with { Lap = gesture },
            "reset" => shortcuts with { Reset = gesture },
            "export" => shortcuts with { Export = gesture },
            _ => shortcuts,
        };
        SaveTimerShortcuts(shortcuts);
    }
}
