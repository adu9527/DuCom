using DuCom.Core.Ports;
using DuCom.Services;
using DuCom.ViewModels;
using DuCom.PluginHost.Core;

namespace DuCom;

internal sealed class CompositionRoot : IAsyncDisposable
{
    private readonly MainViewModel _mainViewModel;
    private readonly UiResponsivenessMonitor _uiResponsivenessMonitor;
    private readonly SerialLeaseCoordinator _serialLeases = new();

    public CompositionRoot()
    {
        MainViewModel? mainViewModel = null;
        mainViewModel = new MainViewModel(
            new WindowsPortDiscovery(),
            options => new LeaseAwareWorkspaceSession(new SerialWorkspaceSession(
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
                options.TimestampFormat,
                () => Math.Max(1, mainViewModel!.PrivateMemoryThresholdMiB) * 1024L * 1024L), _serialLeases));
        _mainViewModel = mainViewModel;
        PrivateMemoryMonitorService memoryMonitor = new(
            () => _mainViewModel.PrivateMemoryMonitorEnabled,
            () => Math.Max(1, _mainViewModel.PrivateMemoryThresholdMiB),
            (name, exception) => Program.DiagnosticLog?.Error($"Background service '{name}' failed.", exception));
        _mainViewModel.AttachPrivateMemoryMonitor(memoryMonitor);
        memoryMonitor.Start();
        _uiResponsivenessMonitor = new UiResponsivenessMonitor(System.Windows.Application.Current.Dispatcher);
        _uiResponsivenessMonitor.Start();
    }

    public MainWindow CreateMainWindow() => new(_mainViewModel);

    public ViewModels.MainViewModel MainViewModel => _mainViewModel;
    public SerialLeaseCoordinator SerialLeases => _serialLeases;

    public async ValueTask DisposeAsync()
    {
        await _uiResponsivenessMonitor.DisposeAsync().ConfigureAwait(false);
        await _mainViewModel.DisposeAsync().ConfigureAwait(false);
    }
}
