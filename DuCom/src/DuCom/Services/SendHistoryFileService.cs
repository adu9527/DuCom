using System.IO;
using System.Text.Json;
using DuCom.Core.Persistence;
using DuCom.Core.Sending;

namespace DuCom.Services;

/// <summary>
/// Thin persistence shim for the global send-history list stored as a JSON string array.
/// </summary>
public static class SendHistoryFileService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DuCom",
        "send-history.json");

    public static void LoadInto(SendHistory history)
    {
        ArgumentNullException.ThrowIfNull(history);
        if (!File.Exists(FilePath))
        {
            return;
        }

        try
        {
            List<string>? entries = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(FilePath), JsonOptions);
            if (entries is not null)
            {
                history.Replace(entries);
            }
        }
        catch (Exception exception)
        {
            Program.DiagnosticLog?.Warning($"Failed to load send history from {FilePath}. {exception.Message}");
        }
    }

    public static void Save(SendHistory history)
    {
        ArgumentNullException.ThrowIfNull(history);
        AtomicFileStore.WriteAllText(FilePath, JsonSerializer.Serialize(history.Entries, JsonOptions));
    }
}
