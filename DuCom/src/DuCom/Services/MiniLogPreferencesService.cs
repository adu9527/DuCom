using System.IO;
using System.Text.Json;
using DuCom.Core.Persistence;
using DuCom.Core.Sending;

namespace DuCom.Services;

public static class MiniLogPreferencesService
{
    private static readonly object SyncRoot = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DuCom",
        "mini-log-preferences.json");

    public static MiniLogPreferences Load(string portName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(portName);
        try
        {
            lock (SyncRoot)
            {
                string? json = File.Exists(FilePath) ? File.ReadAllText(FilePath) : null;
                if (string.IsNullOrEmpty(json))
                {
                    return new();
                }

                try
                {
                    Dictionary<string, MiniLogPreferences>? stored =
                        JsonSerializer.Deserialize<Dictionary<string, MiniLogPreferences>>(json);
                    if (stored is not null && stored.TryGetValue(portName, out MiniLogPreferences? preferences))
                    {
                        return preferences;
                    }
                }
                catch (JsonException)
                {
                    // The previous format stored one global Topmost preference.
                    return JsonSerializer.Deserialize<MiniLogPreferences>(json) ?? new();
                }

                return new();
            }
        }
        catch (Exception exception)
        {
            Program.DiagnosticLog?.Warning($"Failed to load mini log preferences. {exception.Message}");
            return new();
        }
    }

    public static void Save(string portName, MiniLogPreferences preferences)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(portName);
        try
        {
            lock (SyncRoot)
            {
                Dictionary<string, MiniLogPreferences> preferencesByPort = LoadAll();
                preferencesByPort[portName] = preferences;
                AtomicFileStore.WriteAllText(FilePath, JsonSerializer.Serialize(preferencesByPort, JsonOptions));
            }
        }
        catch (Exception exception)
        {
            Program.DiagnosticLog?.Warning($"Failed to save mini log preferences. {exception.Message}");
        }
    }

    private static Dictionary<string, MiniLogPreferences> LoadAll()
    {
        if (!File.Exists(FilePath))
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            Dictionary<string, MiniLogPreferences>? stored =
                JsonSerializer.Deserialize<Dictionary<string, MiniLogPreferences>>(File.ReadAllText(FilePath));
            return stored is null
                ? new(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, MiniLogPreferences>(stored, StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            // Saving the first per-port entry upgrades the previous global format.
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }
}

public sealed record MiniLogPreferences(
    double? Left = null,
    double? Top = null,
    double Width = 520,
    double Height = 360,
    bool Topmost = true,
    bool Follow = true,
    SendMode SendMode = SendMode.Str,
    NewlinePolicy Newline = NewlinePolicy.None);

public sealed record FloatSendGlobalPreferences(
    int ReplyWindowMs = FloatSendGlobalPreferencesService.DefaultReplyWindowMs,
    bool Topmost = false);

/// <summary>
/// Float-send window preferences shared by every port window: the reply-window duration
/// (the window after a send during which replies render in the sent mode) and the
/// always-on-top state. Persisted globally, matching the reference tool.
/// </summary>
public static class FloatSendGlobalPreferencesService
{
    public const int DefaultReplyWindowMs = 2_000;
    public const int MinimumReplyWindowMs = 100;
    public const int MaximumReplyWindowMs = 60_000;

    private static readonly object SyncRoot = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DuCom",
        "float-send-global.json");

    public static FloatSendGlobalPreferences Load()
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

                return JsonSerializer.Deserialize<FloatSendGlobalPreferences>(json) ?? new();
            }
        }
        catch (Exception exception)
        {
            Program.DiagnosticLog?.Warning($"Failed to load float send global preferences. {exception.Message}");
            return new();
        }
    }

    public static void Save(FloatSendGlobalPreferences preferences)
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
            Program.DiagnosticLog?.Warning($"Failed to save float send global preferences. {exception.Message}");
        }
    }
}
