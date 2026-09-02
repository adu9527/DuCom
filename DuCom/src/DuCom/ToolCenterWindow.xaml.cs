using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DuCom.Services;
using DuCom.Services.Shortcuts;
using DuCom.ViewModels;
using Wpf.Ui.Controls;

namespace DuCom;

public partial class ToolCenterWindow : FluentWindow, IAsyncDisposable
{
    private readonly ToolCenterViewModel _viewModel;

    public ToolCenterWindow(
        string page,
        ShortcutManager? shortcutManager = null,
        CommandGroupRunnerHost? commandRunnerHost = null,
        MainViewModel? mainViewModel = null,
        TelnetBridgeService? telnetBridge = null)
    {
        InitializeComponent();
        _viewModel = new ToolCenterViewModel(shortcutManager, commandRunnerHost, mainViewModel, telnetBridge);
        _viewModel.SelectedTabIndex = ToolCenterPages.IndexOf(page);
        DataContext = _viewModel;
        PluginManagerHost.DataContext = mainViewModel?.PluginManager;
        Closed += OnClosed;
    }

    private void CommandsGrid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e) =>
        Dispatcher.BeginInvoke(_viewModel.CommitScriptCommandEdits, System.Windows.Threading.DispatcherPriority.Background);

    private void SendHistory_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is System.Windows.Controls.ListBox list && list.SelectedItem is string entry)
        {
            _viewModel.UseSendHistoryEntryCommand.Execute(entry);
        }
    }

    private void UseSendHistory_Click(object sender, RoutedEventArgs e)
    {
        if (((System.Windows.Controls.MenuItem)sender).Parent is ContextMenu menu &&
            menu.PlacementTarget is System.Windows.Controls.ListBox list &&
            list.SelectedItem is string entry)
        {
            _viewModel.UseSendHistoryEntryCommand.Execute(entry);
        }
    }

    private void TelnetPassword_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.PasswordBox passwordBox)
        {
            _viewModel.TelnetPassword = passwordBox.Password;
        }
    }

    private async void OnClosed(object? sender, EventArgs e)
    {
        Closed -= OnClosed;
        await DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _viewModel.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    internal Task SmokeTelnetAsync() => _viewModel.SmokeTelnetAsync();
}
