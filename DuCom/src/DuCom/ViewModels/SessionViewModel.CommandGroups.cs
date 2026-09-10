using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DuCom.Core.Sending;
using DuCom.Core.Storage;
using DuCom.Services;

namespace DuCom.ViewModels;

public partial class SessionViewModel
{
    /// <summary>Command groups available to this session (loaded from the command store).</summary>
    public ObservableCollection<CommandGroup> CommandGroups { get; } = [];

    [ObservableProperty]
    public partial CommandGroup? SelectedCommandGroup { get; set; }

    partial void OnSelectedCommandGroupChanged(CommandGroup? value) =>
        OnPropertyChanged(nameof(SelectedGroupCommands));

    /// <summary>Ordered commands of the selected group; re-computed on selection change.</summary>
    public IReadOnlyList<ScriptCommand> SelectedGroupCommands =>
        SelectedCommandGroup?.OrderedCommands() ?? [];

    [ObservableProperty]
    public partial bool IsCommandGroupRunning { get; private set; }

    private void OnCommandGroupHostStateChanged(object? sender, EventArgs e) =>
        IsCommandGroupRunning = _commandGroupHost.IsRunning;

    /// <summary>Reloads command groups from the store, keeping the current selection when possible.</summary>
    public void RefreshCommandGroups()
    {
        Guid? previousId = SelectedCommandGroup?.Id;
        CommandGroups.Clear();
        foreach (CommandGroup group in CommandScriptStore.Load())
        {
            CommandGroups.Add(group);
        }

        SelectedCommandGroup = previousId.HasValue
            ? CommandGroups.FirstOrDefault(group => group.Id == previousId.Value)
            : CommandGroups.FirstOrDefault(group => string.Equals(group.Name, DefaultDuComData.MyProjectName, StringComparison.Ordinal))
                ?? CommandGroups.FirstOrDefault();
    }

    /// <summary>Starts or stops looping the selected command group against this session.</summary>
    [RelayCommand]
    private async Task ToggleCommandGroupRunAsync()
    {
        if (IsCommandGroupRunning)
        {
            await _commandGroupHost.StopAsync();
            return;
        }

        if (SelectedCommandGroup is not null)
        {
            _commandGroupHost.Start(SelectedCommandGroup);
        }
    }

    /// <summary>Sends one scripted command immediately through this session.</summary>
    [RelayCommand]
    private async Task SendScriptCommandAsync(ScriptCommand? command)
    {
        if (command is null || !IsOpen)
        {
            return;
        }

        await _session.SendAsync(
            command.IsHex ? SendMode.Hex : SendMode.Str,
            command.Payload,
            command.Newline);
    }
}
