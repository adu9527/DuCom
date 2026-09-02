using System.IO;
using DuCom.Core.Persistence;
using DuCom.Core.Sending;

namespace DuCom.Services;

/// <summary>Persistence for all command groups under %LocalAppData%\DuCom\command-scripts.json.</summary>
public static class CommandScriptStore
{
    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DuCom",
        "command-scripts.json");

    public static IReadOnlyList<CommandGroup> Load()
    {
        if (!File.Exists(FilePath))
        {
            IReadOnlyList<CommandGroup> defaults = DefaultDuComData.MergeCommandGroups([], out _);
            Save(defaults);
            return defaults;
        }

        try
        {
            IReadOnlyList<CommandGroup> groups = CommandScriptSerializer.Deserialize(File.ReadAllText(FilePath), out IReadOnlyList<string> warnings);
            foreach (string warning in warnings)
            {
                Program.DiagnosticLog?.Warning($"Command script import warning. {warning}");
            }

            IReadOnlyList<CommandGroup> merged = DefaultDuComData.MergeCommandGroups(groups, out bool changed);
            if (changed)
            {
                Save(merged);
            }

            return merged;
        }
        catch (Exception exception)
        {
            Program.DiagnosticLog?.Warning($"Failed to load command scripts from {FilePath}. {exception.Message}");
            // Preserve the unreadable user file for recovery, but keep the UI usable.
            return DefaultDuComData.MergeCommandGroups([], out _);
        }
    }

    public static bool Save(IReadOnlyList<CommandGroup> groups)
    {
        try
        {
            AtomicFileStore.WriteAllText(FilePath, CommandScriptSerializer.Serialize(groups));
            return true;
        }
        catch (Exception exception)
        {
            Program.DiagnosticLog?.Warning($"Failed to save command scripts to {FilePath}. {exception.Message}");
            return false;
        }
    }
}
