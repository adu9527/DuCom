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

}
