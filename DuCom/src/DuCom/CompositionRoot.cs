using DuCom.Core.Ports;
using DuCom.Services;
using DuCom.ViewModels;

namespace DuCom;

internal sealed class CompositionRoot : IAsyncDisposable
{
    private readonly MainViewModel _mainViewModel;

    public CompositionRoot()
    {
        _mainViewModel = new MainViewModel(
            new WindowsPortDiscovery(),
            options => new SerialWorkspaceSession(
                options.PortSettings,
                options.ReceiveDisplayMode,
                options.TimestampEnabled,
                options.LoggingEnabled,
                options.LogDirectory,
                options.LogRotationBytes,
                options.LogRotationEnabled,
                options.DisplayBudgetBytes,
                options.LogFileNameFormat,
                options.SendPrefixEnabled,
                options.SendPrefix,
                options.TimestampFormat));
        PrivateMemoryMonitorService memoryMonitor = new(
            () => _mainViewModel.PrivateMemoryMonitorEnabled,
            () => Math.Max(1, _mainViewModel.PrivateMemoryThresholdMiB),
            (name, exception) => Program.DiagnosticLog?.Error($"Background service '{name}' failed.", exception));
        _mainViewModel.AttachPrivateMemoryMonitor(memoryMonitor);
        memoryMonitor.Start();
    }

    public MainWindow CreateMainWindow() => new(_mainViewModel);

    public ValueTask DisposeAsync() => _mainViewModel.DisposeAsync();
}
