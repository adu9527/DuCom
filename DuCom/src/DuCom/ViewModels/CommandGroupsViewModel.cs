using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DuCom.Core.Sending;
using DuCom.Services;

namespace DuCom.ViewModels;

/// <summary>
/// Editor for command groups ("projects"). Extracted from the tool center into its own
/// window; every destructive or naming action goes through a dialog so a click always
/// produces visible feedback instead of silently doing nothing.
/// </summary>
public partial class CommandGroupsViewModel : ObservableObject, IAsyncDisposable
{
    private const int CommitDebounceMilliseconds = 400;

    private readonly CommandGroupRunnerHost? _commandRunner;
    private readonly MainViewModel? _mainViewModel;
    private readonly System.Windows.Threading.DispatcherTimer _portTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly System.Windows.Threading.DispatcherTimer _commitTimer = new() { Interval = TimeSpan.FromMilliseconds(CommitDebounceMilliseconds) };
    private List<CommandGroup> _commandGroups = [];
    private bool _commitPending;

    public CommandGroupsViewModel(CommandGroupRunnerHost? commandRunnerHost, MainViewModel? mainViewModel)
    {
        _commandRunner = commandRunnerHost;
        _mainViewModel = mainViewModel;
        if (_commandRunner is not null)
        {
            _commandRunner.StateChanged += OnRunnerStateChanged;
            _commandRunner.CommandStatusChanged += OnCommandStatusChanged;
        }

        LoadCommandGroups();
        RefreshCommandTargetPorts();
        IsRunnerRunning = _commandRunner?.IsRunning ?? false;
        _portTimer.Tick += (_, _) => RefreshCommandTargetPorts();
        _portTimer.Start();
        _commitTimer.Tick += (_, _) => FlushCommit();
    }

    public ObservableCollection<CommandGroupRow> CommandGroups { get; } = [];

    public ObservableCollection<CommandGroupRow> FilteredCommandGroups { get; } = [];

    public ObservableCollection<ScriptCommandRow> SelectedCommands { get; } = [];

    public ObservableCollection<CommandTargetPortRow> CommandTargetPorts { get; } = [];

    public IReadOnlyList<NewlinePolicy> NewlineOptions { get; } =
        [NewlinePolicy.None, NewlinePolicy.Cr, NewlinePolicy.Lf, NewlinePolicy.CrLf];

    [ObservableProperty]
    public partial string GroupSearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial CommandGroupRow? SelectedCommandGroup { get; set; }

    [ObservableProperty]
    public partial ScriptCommandRow? SelectedScriptCommand { get; set; }

    [ObservableProperty]
    public partial bool IsRunnerRunning { get; private set; }

    [ObservableProperty]
    public partial string CommandMessage { get; private set; } = string.Empty;

    partial void OnSelectedCommandGroupChanged(CommandGroupRow? value) => LoadSelectedCommands();

    partial void OnSelectedCommandGroupChanging(CommandGroupRow? oldValue, CommandGroupRow? newValue) =>
        FlushCommit();

    partial void OnSelectedScriptCommandChanged(ScriptCommandRow? oldValue, ScriptCommandRow? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.PropertyChanged -= OnCommandRowChanged;
        }

        if (newValue is not null)
        {
            newValue.PropertyChanged += OnCommandRowChanged;
        }

        OnPropertyChanged(nameof(HasSelectedCommand));
    }

    public bool HasSelectedCommand => SelectedScriptCommand is not null;

    private void OnCommandRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => ScheduleCommit();

    /// <summary>Edits arrive one keystroke at a time; persist shortly after they stop.</summary>
    private void ScheduleCommit()
    {
        _commitPending = true;
        _commitTimer.Stop();
        _commitTimer.Start();
    }

    private void FlushCommit()
    {
        _commitTimer.Stop();
        if (!_commitPending)
        {
            return;
        }

        _commitPending = false;
        CommitRowsToGroup();
    }

    partial void OnGroupSearchTextChanged(string value) => RefreshFilteredGroups();

    public string GroupSummary => Resource("Commands.GroupSummary", CommandGroups.Count);

    private void RefreshFilteredGroups()
    {
        string text = GroupSearchText.Trim();
        FilteredCommandGroups.Clear();
        foreach (CommandGroupRow row in CommandGroups.Where(row =>
                     string.IsNullOrEmpty(text) || row.Name.Contains(text, StringComparison.OrdinalIgnoreCase)))
        {
            FilteredCommandGroups.Add(row);
        }

        if (SelectedCommandGroup is not null && !FilteredCommandGroups.Contains(SelectedCommandGroup))
        {
            SelectedCommandGroup = FilteredCommandGroups.FirstOrDefault();
        }

        OnPropertyChanged(nameof(GroupSummary));
    }

    private static string GetResourceString(string key) =>
        Application.Current?.TryFindResource(key) as string ?? key;

    private static string Resource(string key, params object?[] args)
    {
        string text = GetResourceString(key);
        foreach ((int index, object? arg) in args.Select((arg, index) => (index, arg)))
        {
            text = text.Replace($"{{{index}}}", arg?.ToString(), StringComparison.Ordinal);
        }

        return text;
    }

    private static Window? HostWindow => Application.Current?.Windows.OfType<CommandGroupsWindow>().FirstOrDefault();

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

    [RelayCommand]
    private void StartSelectedCommandGroup()
    {
        if (_commandRunner is null)
        {
            return;
        }

        if (FindGroup(SelectedCommandGroup?.GroupId) is not { } group || group.Commands.Count == 0)
        {
            CommandMessage = GetResourceString("Status.CommandRunNoSession");
            return;
        }

        // Opening a port is asynchronous; the host re-validates the live session itself.
        if (!_commandRunner.Start(group))
        {
            return;
        }

        IsRunnerRunning = true;
    }

    [RelayCommand]
    private Task StopSelectedCommandGroupAsync() =>
        _commandRunner?.StopAsync() ?? Task.CompletedTask;

    private void RefreshCommandTargetPorts()
    {
        if (_mainViewModel is null)
        {
            return;
        }

        Dictionary<string, CommandTargetPortRow> existing = CommandTargetPorts.ToDictionary(row => row.PortName, StringComparer.OrdinalIgnoreCase);
        string[] names = [.. _mainViewModel.AvailablePorts.Select(port => port.PortName)
            .Concat(_mainViewModel.Sessions.Select(session => session.PortName))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(name => name, StringComparer.Ordinal)];

        CommandTargetPorts.Clear();
        foreach (string name in names)
        {
            bool isOpen = _mainViewModel.Sessions.Any(session =>
                session.IsOpen && string.Equals(session.PortName, name, StringComparison.OrdinalIgnoreCase));
            CommandTargetPortRow row = existing.TryGetValue(name, out CommandTargetPortRow? current)
                ? current
                : new CommandTargetPortRow(name, OnCommandTargetSelectionChanged);
            bool isSelected = existing.ContainsKey(name)
                ? row.IsSelected
                : _mainViewModel.CommandTargetPortNames.Contains(name, StringComparer.OrdinalIgnoreCase);
            row.Update(isSelected, isOpen);
            CommandTargetPorts.Add(row);
        }
    }

    private void OnCommandTargetSelectionChanged()
    {
        _mainViewModel?.SetCommandTargetPortNames(CommandTargetPorts
            .Where(row => row.IsSelected)
            .Select(row => row.PortName));
    }

    private void OnRunnerStateChanged(object? sender, EventArgs e) =>
        App.Current.Dispatcher.BeginInvoke(() =>
        {
            bool wasRunning = IsRunnerRunning;
            IsRunnerRunning = _commandRunner?.IsRunning ?? false;
            if (wasRunning && !IsRunnerRunning)
            {
                RebuildGroupRows();
            }
        });

    private void OnCommandStatusChanged(object? sender, ScriptCommandStatusEventArgs e) =>
        App.Current.Dispatcher.BeginInvoke(() =>
        {
            ScriptCommandRow? row = SelectedCommands.FirstOrDefault(item => item.Id == e.CommandId);
            if (row is not null)
            {
                row.SetState(e.TargetName, GetResourceString($"Commands.State.{e.State}"));
                if (!string.IsNullOrWhiteSpace(e.ErrorMessage))
                {
                    CommandMessage = Resource("Commands.TargetError", e.TargetName, e.ErrorMessage);
                }
            }
        });

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

    public async ValueTask DisposeAsync()
    {
        _portTimer.Stop();
        FlushCommit();
        if (_commandRunner is not null)
        {
            // Observation only: the shared host is owned by MainViewModel and must survive
            // closing this editor window. Any active run keeps running for the main window.
            _commandRunner.StateChanged -= OnRunnerStateChanged;
            _commandRunner.CommandStatusChanged -= OnCommandStatusChanged;
        }

        GC.SuppressFinalize(this);
    }
}
