using System.IO;
using System.Text.Json;
using System.Windows.Input;

namespace DuCom.Services.Plugins;

public sealed record StopwatchShortcuts
{
    public string ToggleRun { get; init; } = "Space";
    public string Lap { get; init; } = "L";
    public string Reset { get; init; } = "R";

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DuCom", "stopwatch-shortcuts.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static StopwatchShortcuts? _cached;

    public static StopwatchShortcuts Load()
    {
        if (_cached is not null)
        {
            return _cached;
        }

        try
        {
            if (File.Exists(FilePath)
                && JsonSerializer.Deserialize<StopwatchShortcuts>(File.ReadAllText(FilePath), JsonOptions) is { } loaded)
            {
                return _cached = loaded;
            }
        }
        catch (Exception exception)
        {
            Program.DiagnosticLog?.Warning(
                "Stopwatch shortcut file could not be loaded; default shortcuts apply for this launch.",
                exception);
        }

        return _cached = new StopwatchShortcuts();
    }

    public static void Save(StopwatchShortcuts shortcuts)
    {
        _cached = shortcuts;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(shortcuts, JsonOptions));
        }
        catch (Exception exception)
        {
            // Shortcut persistence is best-effort; the in-memory mapping still applies.
            Program.DiagnosticLog?.Warning("Stopwatch shortcut file could not be saved.", exception);
        }
    }

    public static bool TryParse(string gesture, out Key key, out ModifierKeys modifiers)
    {
        key = Key.None;
        modifiers = ModifierKeys.None;
        if (string.IsNullOrWhiteSpace(gesture))
        {
            return false;
        }

        foreach (string part in gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.Equals(part, "Ctrl", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= ModifierKeys.Control;
            }
            else if (string.Equals(part, "Shift", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= ModifierKeys.Shift;
            }
            else if (string.Equals(part, "Alt", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= ModifierKeys.Alt;
            }
            else if (Enum.TryParse(part, ignoreCase: true, out key) && Enum.IsDefined(key))
            {
                // Parsed the final key.
            }
            else
            {
                return false;
            }
        }

        return key != Key.None;
    }

    public static string Display(string gesture)
    {
        if (!TryParse(gesture, out Key key, out _))
        {
            return gesture;
        }

        return key switch
        {
            Key.Space => "Space",
            Key.OemPlus => "+",
            Key.OemMinus => "-",
            _ => key.ToString(),
        };
    }

    public static string FromEventArgs(ModifierKeys modifiers, Key key)
    {
        string prefix = string.Empty;
        if ((modifiers & ModifierKeys.Control) == ModifierKeys.Control) prefix += "Ctrl+";
        if ((modifiers & ModifierKeys.Alt) == ModifierKeys.Alt) prefix += "Alt+";
        if ((modifiers & ModifierKeys.Shift) == ModifierKeys.Shift) prefix += "Shift+";
        return prefix + key;
    }
}
