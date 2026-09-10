using CommunityToolkit.Mvvm.Input;
using DuCom.Core.Sending;
using DuCom.Services;

namespace DuCom.ViewModels;

public partial class CommandGroupsViewModel
{
    private void LoadSelectedCommands()
    {
        // A group switch invalidates the previous row object; keeping it selected
        // would make the detail editor modify a row no longer in this collection.
        SelectedScriptCommand = null;
        SelectedCommands.Clear();
        CommandGroup? group = FindGroup(SelectedCommandGroup?.GroupId);
        if (group is null)
        {
            return;
        }

        foreach (ScriptCommand command in group.OrderedCommands())
        {
            SelectedCommands.Add(ScriptCommandRow.From(command));
        }

        SelectedScriptCommand = SelectedCommands.FirstOrDefault();
    }

    private CommandGroup? FindGroup(Guid? id) =>
        id is null ? null : _commandGroups.FirstOrDefault(group => group.Id == id.Value);

    private void LoadCommandGroups()
    {
        _commandGroups = [.. CommandScriptStore.Load()];
        RebuildGroupRows();
    }

    private void RebuildGroupRows()
    {
        Guid? keep = SelectedCommandGroup?.GroupId;
        CommandGroups.Clear();
        foreach (CommandGroup group in _commandGroups)
        {
            CommandGroupRow row = new(group.Id, group.Name, group.Commands.Count)
            {
                IsRunning = _commandRunner is { RunningGroup.Id: var runningId } && runningId == group.Id,
            };
            CommandGroups.Add(row);
        }

        RefreshFilteredGroups();
        SelectedCommandGroup = FilteredCommandGroups.FirstOrDefault(row => row.GroupId == keep) ?? FilteredCommandGroups.FirstOrDefault();
    }

    [RelayCommand]
    private void AddCommandGroup()
    {
        string initial = UniqueName(GetResourceString("Commands.DefaultGroupName"));
        string? name = ThemedInputDialog.Prompt(
            HostWindow,
            GetResourceString("Commands.NewGroupPrompt"),
            GetResourceString("Commands.NewGroup"),
            initial,
            ValidateGroupName);
        if (name is null)
        {
            return;
        }

        _commandGroups.Add(CommandGroup.Create(name));
        if (!PersistGroups())
        {
            return;
        }

        RebuildGroupRows();
        SelectRow(name);
        CommandMessage = string.Empty;
    }

    [RelayCommand]
    private void RenameSelectedCommandGroup()
    {
        if (FindGroup(SelectedCommandGroup?.GroupId) is not { } group)
        {
            CommandMessage = GetResourceString("Commands.NoGroupSelected");
            return;
        }

        string? name = ThemedInputDialog.Prompt(
            HostWindow,
            GetResourceString("Commands.RenameGroupPrompt"),
            GetResourceString("Commands.RenameGroup"),
            group.Name,
            value => string.Equals(value.Trim(), group.Name, StringComparison.Ordinal) ? null : ValidateGroupName(value));
        if (name is null || string.Equals(name, group.Name, StringComparison.Ordinal))
        {
            return;
        }

        _commandGroups[_commandGroups.IndexOf(group)] = group with { Name = name };
        PersistGroups();
        RebuildGroupRows();
        SelectRow(name);
    }

    private string? ValidateGroupName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Commands.NameRequired";
        }

        string name = value.Trim();
        return _commandGroups.Any(group => string.Equals(group.Name, name, StringComparison.OrdinalIgnoreCase))
            ? "Commands.NameDuplicate"
            : null;
    }

    private string UniqueName(string baseName)
    {
        string clean = string.IsNullOrWhiteSpace(baseName) ? GetResourceString("Commands.DefaultGroupName") : baseName.Trim();
        string candidate = clean;
        for (int suffix = 2; _commandGroups.Any(group => string.Equals(group.Name, candidate, StringComparison.OrdinalIgnoreCase)); suffix++)
        {
            candidate = $"{clean} ({suffix})";
        }

        return candidate;
    }

    private void SelectRow(string name)
    {
        SelectedCommandGroup = FilteredCommandGroups.FirstOrDefault(row => row.Name == name);
        if (SelectedCommandGroup is not null && string.Equals(SelectedCommandGroup.Name, name, StringComparison.Ordinal))
        {
            LoadSelectedCommands();
        }
    }

    [RelayCommand]
    private async Task DeleteSelectedCommandGroupAsync()
    {
        if (FindGroup(SelectedCommandGroup?.GroupId) is not { } group)
        {
            CommandMessage = GetResourceString("Commands.NoGroupSelected");
            return;
        }

        bool confirmed = ThemedMessageDialog.Confirm(
            HostWindow,
            Resource("Commands.DeleteGroupPrompt", group.Name, group.Commands.Count),
            GetResourceString("Commands.DeleteGroup"),
            "Dialog.Delete",
            "Dialog.Cancel");
        if (!confirmed)
        {
            return;
        }

        if (_commandRunner is { RunningGroup.Id: var runningId } && runningId == group.Id)
        {
            await _commandRunner.StopAsync();
        }

        _commandGroups.Remove(group);
        PersistGroups();
        RebuildGroupRows();
    }

    [RelayCommand]
    private void AddScriptCommand()
    {
        if (FindGroup(SelectedCommandGroup?.GroupId) is null)
        {
            CommandMessage = GetResourceString("Commands.NoGroupSelected");
            return;
        }

        ScriptCommandRow row = ScriptCommandRow.From(ScriptCommand.Create(
            GetResourceString("Commands.DefaultCommandName"),
            order: NextOrderValue()));
        SelectedCommands.Add(row);
        SelectedScriptCommand = row;
        CommitRowsToGroup();
    }

    private int NextOrderValue() =>
        SelectedCommands.Count == 0 ? 0 : SelectedCommands.Max(row => row.OrderValue) + 1;

    [RelayCommand]
    private void DeleteScriptCommand(ScriptCommandRow? row)
    {
        if (row is null || !SelectedCommands.Remove(row))
        {
            return;
        }

        if (ReferenceEquals(SelectedScriptCommand, row))
        {
            SelectedScriptCommand = null;
        }

        CommitRowsToGroup();
    }

    /// <summary>Called by the view or tests to persist pending edits immediately.</summary>
    public void CommitScriptCommandEdits() => FlushCommit();

    private void CommitRowsToGroup()
    {
        _commitPending = false;
        _commitTimer.Stop();
        CommandGroup? group = FindGroup(SelectedCommandGroup?.GroupId);
        if (group is null)
        {
            return;
        }

        List<ScriptCommand> commands = SelectedCommands.Select((row, index) => row.ToCommand(index)).ToList();
        _commandGroups[_commandGroups.IndexOf(group)] = group with { Commands = commands };
        PersistGroups();
        if (SelectedCommandGroup is not null)
        {
            SelectedCommandGroup.CommandCount = commands.Count;
        }
    }
}
