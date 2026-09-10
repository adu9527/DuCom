using System.IO;
using CommunityToolkit.Mvvm.Input;
using DuCom.Core.Sending;
using DuCom.Services;

namespace DuCom.ViewModels;

public partial class CommandGroupsViewModel
{
    [RelayCommand]
    private void ExportSelectedCommandGroup()
    {
        Microsoft.Win32.SaveFileDialog dialog = new() { Filter = "DuCom command groups (*.json)|*.json", FileName = "command-group.json" };
        if (dialog.ShowDialog() != true || FindGroup(SelectedCommandGroup?.GroupId) is not { } group)
        {
            return;
        }

        try
        {
            File.WriteAllText(dialog.FileName, CommandScriptSerializer.Serialize([group]));
            CommandMessage = Resource("Commands.ExportDone", group.Name);
        }
        catch (Exception exception)
        {
            Program.DiagnosticLog?.Warning("Failed to export command group.", exception);
            CommandMessage = GetResourceString("Commands.ExportFailed");
        }
    }

    [RelayCommand]
    private void ImportCommandGroup()
    {
        Microsoft.Win32.OpenFileDialog dialog = new() { Filter = "DuCom command groups (*.json)|*.json|All files (*.*)|*.*" };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            IReadOnlyList<CommandGroup> parsed = CommandScriptSerializer.Deserialize(File.ReadAllText(dialog.FileName), out IReadOnlyList<string> warnings);
            string? lastImported = null;
            foreach (CommandGroup incoming in parsed)
            {
                string name = UniqueName(incoming.Name);
                _commandGroups.Add(incoming with { Name = name });
                lastImported = name;
            }

            if (!PersistGroups())
            {
                CommandMessage = GetResourceString("Commands.SaveFailed");
                return;
            }

            RebuildGroupRows();
            if (lastImported is not null)
            {
                SelectRow(lastImported);
            }

            CommandMessage = warnings.Count == 0
                ? Resource("Commands.ImportDone", parsed.Count)
                : Resource("Commands.ImportWarnings", warnings.Count);
            foreach (string warning in warnings)
            {
                Program.DiagnosticLog?.Warning($"Command script import warning. {warning}");
            }
        }
        catch (Exception exception)
        {
            Program.DiagnosticLog?.Warning("Failed to import command group.", exception);
            CommandMessage = GetResourceString("Commands.ImportFailed");
        }
    }

    private bool PersistGroups()
    {
        List<CommandGroup> ordered = [.. _commandGroups.OrderBy(group => group.Name, StringComparer.OrdinalIgnoreCase)];
        _commandGroups = ordered;
        bool saved = CommandScriptStore.Save(ordered);
        if (!saved)
        {
            CommandMessage = GetResourceString("Commands.SaveFailed");
        }

        return saved;
    }
}
