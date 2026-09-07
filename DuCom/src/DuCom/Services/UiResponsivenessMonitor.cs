using System.Diagnostics;
using System.Globalization;
using System.Windows.Threading;

namespace DuCom.Services;

internal sealed class UiResponsivenessMonitor : IAsyncDisposable
{
    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan UnresponsiveThreshold = TimeSpan.FromSeconds(2);
    private readonly Dispatcher _dispatcher;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Process _process = Process.GetCurrentProcess();
    private Task? _loopTask;

    public UiResponsivenessMonitor(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public void Start() => _loopTask ??= Task.Run(MonitorAsync);

    private async Task MonitorAsync()
    {
        try
        {
            while (true)
            {
                await Task.Delay(SampleInterval, _cancellation.Token).ConfigureAwait(false);
                long startedAt = Stopwatch.GetTimestamp();
                DispatcherOperation operation = _dispatcher.InvokeAsync(() => { }, DispatcherPriority.Normal);
                Task threshold = Task.Delay(UnresponsiveThreshold, _cancellation.Token);
                bool delayed = await Task.WhenAny(operation.Task, threshold).ConfigureAwait(false) == threshold;
                if (delayed)
                {
                    LogDegraded(Stopwatch.GetElapsedTime(startedAt));
                }

                try
                {
                    await operation.Task.WaitAsync(_cancellation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    operation.Abort();
                    throw;
                }

                if (delayed)
                {
                    TimeSpan duration = Stopwatch.GetElapsedTime(startedAt);
                    Program.DiagnosticLog?.Information(
                        $"UI responsiveness restored. DurationMs={duration.TotalMilliseconds.ToString("0.0", CultureInfo.InvariantCulture)}");
                }
            }
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Program.DiagnosticLog?.Error("UI responsiveness monitor failed.", exception);
        }
    }

    private void LogDegraded(TimeSpan delay)
    {
        _process.Refresh();
        double privateMemoryMiB = _process.PrivateMemorySize64 / 1024d / 1024d;
        double workingSetMiB = _process.WorkingSet64 / 1024d / 1024d;
        Program.DiagnosticLog?.Warning(
            $"UI responsiveness degraded. DelayMs={delay.TotalMilliseconds.ToString("0.0", CultureInfo.InvariantCulture)}; " +
            $"PrivateMemoryMiB={privateMemoryMiB.ToString("0.0", CultureInfo.InvariantCulture)}; " +
            $"WorkingSetMiB={workingSetMiB.ToString("0.0", CultureInfo.InvariantCulture)}");
    }

    public async ValueTask DisposeAsync()
    {
        _cancellation.Cancel();
        if (_loopTask is not null)
        {
            await _loopTask.ConfigureAwait(false);
        }

        _process.Dispose();
        _cancellation.Dispose();
    }
}
