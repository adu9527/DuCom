using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DuCom.Services;
using DuCom.ViewModels;
using Wpf.Ui.Controls;

namespace DuCom;

public partial class CommandGroupsWindow : FluentWindow, IAsyncDisposable
{
    private readonly CommandGroupsViewModel _viewModel;

    public CommandGroupsWindow(CommandGroupRunnerHost? commandRunnerHost, MainViewModel? mainViewModel)
    {
        InitializeComponent();
        _viewModel = new CommandGroupsViewModel(commandRunnerHost, mainViewModel);
        DataContext = _viewModel;
        Closed += OnClosed;
    }

    private void AddCommandButton_Click(object sender, RoutedEventArgs e) =>
        Dispatcher.BeginInvoke(() =>
        {
            CommandNameBox.Focus();
            CommandNameBox.SelectAll();
        });

    private void GroupsList_MouseDoubleClick(object sender, MouseButtonEventArgs e) =>
        _viewModel.RenameSelectedCommandGroupCommand.Execute(null);

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
}
