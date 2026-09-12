using System.Diagnostics;

namespace DuCom.Services;

internal sealed record PortDiscoverySnapshot(
    string[] Names,
    IReadOnlyDictionary<string, DiscoveredPort> Details);

/// <summary>Coalesces serial-port refresh requests into one non-overlapping refresh loop.</summary>
internal sealed class PortRefreshCoordinator
{
    private readonly object _sync = new();
    private readonly Func<Task<PortDiscoverySnapshot>> _discoverAsync;
    private readonly Action<PortDiscoverySnapshot> _apply;
    private readonly Func<bool> _isStopping;
    private readonly Action<TimeSpan, int> _logSlowOperation;
    private readonly Action<Exception> _logException;
    private readonly TimeSpan _slowOperationThreshold;
    private bool _requested;
    private Task? _currentTask;

    public PortRefreshCoordinator(
        Func<Task<PortDiscoverySnapshot>> discoverAsync,
        Action<PortDiscoverySnapshot> apply,
        Func<bool> isStopping,
        Action<TimeSpan, int> logSlowOperation,
        Action<Exception> logException,
        TimeSpan slowOperationThreshold)
    {
        _discoverAsync = discoverAsync ?? throw new ArgumentNullException(nameof(discoverAsync));
        _apply = apply ?? throw new ArgumentNullException(nameof(apply));
        _isStopping = isStopping ?? throw new ArgumentNullException(nameof(isStopping));
        _logSlowOperation = logSlowOperation ?? throw new ArgumentNullException(nameof(logSlowOperation));
        _logException = logException ?? throw new ArgumentNullException(nameof(logException));
        _slowOperationThreshold = slowOperationThreshold;
    }

    public Task? CurrentTask
    {
        get
        {
            lock (_sync)
            {
                return _currentTask;
            }
        }
    }

    public Task RequestRefreshAsync()
    {
        lock (_sync)
        {
            _requested = true;
            if (_currentTask is not null)
            {
                return _currentTask;
            }

            TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _currentTask = completion.Task;
            _ = RunRefreshLoopAsync(completion);
            return completion.Task;
        }
    }

    public Task WaitForCurrentRefreshAsync()
    {
        lock (_sync)
        {
            return _currentTask ?? Task.CompletedTask;
        }
    }

    private async Task RunRefreshLoopAsync(TaskCompletionSource completion)
    {
        try
        {
            while (!_isStopping())
            {
                lock (_sync)
                {
                    _requested = false;
                }

                Stopwatch stopwatch = Stopwatch.StartNew();
                PortDiscoverySnapshot discovered = await _discoverAsync();
                stopwatch.Stop();
                if (stopwatch.Elapsed >= _slowOperationThreshold)
                {
                    _logSlowOperation(stopwatch.Elapsed, discovered.Names.Length);
                }

                lock (_sync)
                {
                    if (_requested)
                    {
                        continue;
                    }
                }

                if (_isStopping())
                {
                    break;
                }

                _apply(discovered);

                lock (_sync)
                {
                    if (_requested)
                    {
                        continue;
                    }

                    _currentTask = null;
                }

                completion.TrySetResult();
                return;
            }
        }
        catch (Exception exception)
        {
            try
            {
                _logException(exception);
            }
            finally
            {
                ClearCurrentTask();
                completion.TrySetResult();
            }

            return;
        }

        ClearCurrentTask();
        completion.TrySetResult();
    }

    private void ClearCurrentTask()
    {
        lock (_sync)
        {
            _currentTask = null;
        }
    }
}
