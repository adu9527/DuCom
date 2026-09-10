using CommunityToolkit.Mvvm.Input;
using DuCom.Core.Sending;

namespace DuCom.ViewModels;

public partial class CommandGroupsViewModel
{
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
