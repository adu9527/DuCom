using System.Windows;
using System.Windows.Input;
using DuCom.ViewModels;
using CommunityToolkit.Mvvm.Input;

namespace DuCom.Services.Shortcuts;

public sealed class ShortcutEngine
{
    private readonly ShortcutManager _manager;
    private readonly MainViewModel _viewModel;
    private readonly Window _window;

    public ShortcutEngine(ShortcutManager manager, MainViewModel viewModel, Window window)
    {
        _manager = manager;
        _viewModel = viewModel;
        _window = window;
    }

    public bool TryHandleKey(Key key, ModifierKeys modifiers)
    {
        var gesture = new ShortcutKeyGesture(key.ToString(), MapModifiers(modifiers));
        string? actionId = _manager.FindActionId(gesture);
        if (actionId is null)
        {
            return false;
        }

        return TryExecuteAction(actionId);
    }

    private bool TryExecuteAction(string actionId)
    {
        try
        {
            ICommand? command = GetCommand(actionId);
            if (command is null || !command.CanExecute(null))
            {
                return false;
            }

            command.Execute(null);
            return true;
        }
        catch (Exception exception)
        {
            Program.DiagnosticLog?.Error($"Shortcut execution failed. ActionId={actionId}", exception);
            return false;
        }
    }

    private ICommand? GetCommand(string actionId) => actionId switch
    {
        "OpenCloseSelectedPort" => ResolveTogglePortCommand(),
        "RefreshPorts" => _viewModel.RefreshPortsCommand,
        "ClearDisplay" => _viewModel.ClearDisplayCommand,
        "SaveVisibleLog" => _viewModel.SaveVisibleLogCommand,
        "ToggleFollowEnd" => _viewModel.ToggleFollowEndCommand,
        "ToggleSidebar" => _viewModel.ToggleSidebarCommand,
        "OpenSearch" => new RelayCommand(OpenSearch),
        "OpenTools" => new RelayCommand(() => _viewModel.OpenSettingsCategory(7)),
        "MaximizeRestore" => new RelayCommand(ToggleWindowState),
        "FocusSendEditor" => new RelayCommand(FocusSendEditor),
        "CloseRightPane" => _viewModel.CloseRightPaneCommand,
        "CloseSelectedSession" => _viewModel.CloseCommand,
        "ToggleHexDisplay" => _viewModel.ToggleDefaultReceiveModeCommand,
        "ToggleTimestamp" => _viewModel.ToggleDefaultTimestampCommand,
        "ToggleSendMode" => _viewModel.ToggleSelectedSendModeCommand,
        "FormatJson" => _viewModel.FormatJsonCommand,
        "JoinLines" => _viewModel.JoinLinesCommand,
        _ => null,
    };

    private ICommand ResolveTogglePortCommand()
    {
        if (_viewModel.SelectedSession is { IsOpen: true })
        {
            return _viewModel.CloseCommand;
        }

        return _viewModel.OpenCommand;
    }

    private void ToggleWindowState()
    {
        _window.WindowState = _window.WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void FocusSendEditor()
    {
        if (_window is MainWindow mainWindow)
        {
            mainWindow.FocusSendEditor();
        }
    }

    private void OpenSearch()
    {
        SessionViewModel? session = _viewModel.SelectedSession ?? _viewModel.SelectedRightSession;
        if (session is not null)
        {
            _viewModel.OpenSearchFor(session);
        }
    }

    private static ShortcutModifiers MapModifiers(ModifierKeys modifiers)
    {
        ShortcutModifiers result = ShortcutModifiers.None;
        if ((modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            result |= ShortcutModifiers.Ctrl;
        }

        if ((modifiers & ModifierKeys.Alt) == ModifierKeys.Alt)
        {
            result |= ShortcutModifiers.Alt;
        }

        if ((modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
        {
            result |= ShortcutModifiers.Shift;
        }

        if ((modifiers & ModifierKeys.Windows) == ModifierKeys.Windows)
        {
            result |= ShortcutModifiers.Win;
        }

        return result;
    }
}
