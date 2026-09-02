using System.Diagnostics;
using DuCom.Core.Diagnostics;

namespace DuCom.Services;

/// <summary>
/// Optional composition service for SuperCom MemoryDog-compatible private-memory checks.
/// It raises immutable samples every ten seconds and does not share behavior or state with
/// the content watchdog.
/// </summary>
public sealed class PrivateMemoryMonitorService : IAsyncDisposable
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(10);

    private readonly Process _process;
    private readonly Func<bool> _enabled;
    private readonly Func<long> _thresholdMegabytes;
    private readonly PeriodicBackgroundWorker _worker;

    public PrivateMemoryMonitorService(
        Func<bool> enabled,
        Func<long> thresholdMegabytes,
        Action<string, Exception>? log = null,
        TimeSpan? interval = null)
    {
        _enabled = enabled ?? throw new ArgumentNullException(nameof(enabled));
        _thresholdMegabytes = thresholdMegabytes ?? throw new ArgumentNullException(nameof(thresholdMegabytes));
        _process = Process.GetCurrentProcess();
        _worker = new PeriodicBackgroundWorker(
            "private-memory-monitor",
            interval ?? DefaultInterval,
            TickAsync,
            log);
    }

    public event EventHandler<PrivateMemoryThresholdSnapshot>? Sampled;

    public event EventHandler<PrivateMemoryThresholdSnapshot>? ThresholdReached;

    public void Start() => _worker.Start();

    private Task TickAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_enabled())
        {
            return Task.CompletedTask;
        }

        _process.Refresh();
        PrivateMemoryThresholdSnapshot snapshot = PrivateMemoryThresholdEvaluator.Evaluate(
            _process.PrivateMemorySize64,
            _thresholdMegabytes());
        Sampled?.Invoke(this, snapshot);
        if (snapshot.IsThresholdReached)
        {
            ThresholdReached?.Invoke(this, snapshot);
        }

        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _worker.DisposeAsync().ConfigureAwait(false);
        _process.Dispose();
    }
}
