using System.Diagnostics;
using DuCom.ViewModels;
using Wpf.Ui.Controls;

namespace DuCom;

public partial class AboutWindow : FluentWindow, IDisposable
{
    private readonly AboutViewModel _viewModel = new();

    public AboutWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        Closed += OnClosed;
    }

    private void Close_Click(object sender, System.Windows.RoutedEventArgs e) => Close();

    private void GitHub_Click(object sender, System.Windows.RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(_viewModel.GitHubUrl) { UseShellExecute = true });

    public void Dispose()
    {
        Closed -= OnClosed;
        _viewModel.Dispose();
        GC.SuppressFinalize(this);
    }

    private void OnClosed(object? sender, EventArgs e) => Dispose();
}
