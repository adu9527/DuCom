using DuCom.Services;
using Xunit;

namespace DuCom.App.Tests;

public sealed class PortRefreshCoordinatorTests
{
    private static readonly TimeSpan DisabledSlowThreshold = TimeSpan.MaxValue;

    [Fact]
    public async Task RequestsDuringDiscoveryMergeIntoCurrentTaskAndRerun()
    {
        TaskCompletionSource<PortDiscoverySnapshot> firstDiscovery = NewDiscoverySource();
        int discoveryCalls = 0;
        int activeDiscoveries = 0;
        int maximumActiveDiscoveries = 0;
        PortRefreshCoordinator coordinator = CreateCoordinator(async () =>
        {
            int active = Interlocked.Increment(ref activeDiscoveries);
            maximumActiveDiscoveries = Math.Max(maximumActiveDiscoveries, active);
            try
            {
                if (Interlocked.Increment(ref discoveryCalls) == 1)
                {
                    return await firstDiscovery.Task;
                }

                return Snapshot("COM2");
            }
            finally
            {
                Interlocked.Decrement(ref activeDiscoveries);
            }
        });

        Task first = coordinator.RequestRefreshAsync();
        Task merged = coordinator.RequestRefreshAsync();

        Assert.Same(first, merged);
        Assert.Same(first, coordinator.CurrentTask);
        firstDiscovery.SetResult(Snapshot("COM1"));
        await first;

        Assert.Equal(2, discoveryCalls);
        Assert.Equal(1, maximumActiveDiscoveries);
        Assert.Null(coordinator.CurrentTask);
    }

    [Fact]
    public async Task NewRequestAfterDiscoveryStartedAppliesCompletedResultAndTrailingResult()
    {
        TaskCompletionSource<PortDiscoverySnapshot> firstDiscovery = NewDiscoverySource();
        List<string> appliedPorts = [];
        int discoveryCalls = 0;
        PortRefreshCoordinator coordinator = CreateCoordinator(
            () => Interlocked.Increment(ref discoveryCalls) == 1
                ? firstDiscovery.Task
                : Task.FromResult(Snapshot("COM2")),
            snapshot => appliedPorts.Add(snapshot.Names.Single()));

        Task refresh = coordinator.RequestRefreshAsync();
        _ = coordinator.RequestRefreshAsync();
        firstDiscovery.SetResult(Snapshot("COM1"));
        await refresh;

        Assert.Equal(["COM1", "COM2"], appliedPorts);
    }

    [Fact]
    public async Task ExceptionClearsCurrentTaskAndAllowsRetry()
    {
        List<Exception> exceptions = [];
        int discoveryCalls = 0;
        int applyCalls = 0;
        PortRefreshCoordinator coordinator = CreateCoordinator(
            () => Interlocked.Increment(ref discoveryCalls) == 1
                ? Task.FromException<PortDiscoverySnapshot>(new InvalidOperationException("failed"))
                : Task.FromResult(Snapshot("COM2")),
            _ => applyCalls++,
            exceptions.Add);

        Task failedRefresh = coordinator.RequestRefreshAsync();
        await failedRefresh;

        Assert.Null(coordinator.CurrentTask);
        Assert.Single(exceptions);

        Task retry = coordinator.RequestRefreshAsync();
        Assert.NotSame(failedRefresh, retry);
        await retry;

        Assert.Equal(2, discoveryCalls);
        Assert.Equal(1, applyCalls);
        Assert.Null(coordinator.CurrentTask);
    }

    [Fact]
    public async Task WaitForCurrentRefreshReturnsCurrentTaskWithoutStartingOne()
    {
        TaskCompletionSource<PortDiscoverySnapshot> discovery = NewDiscoverySource();
        int discoveryCalls = 0;
        PortRefreshCoordinator coordinator = CreateCoordinator(() =>
        {
            discoveryCalls++;
            return discovery.Task;
        });

        await coordinator.WaitForCurrentRefreshAsync();
        Assert.Equal(0, discoveryCalls);

        Task refresh = coordinator.RequestRefreshAsync();
        Task current = coordinator.WaitForCurrentRefreshAsync();
        Assert.Same(refresh, coordinator.CurrentTask);
        Assert.Same(refresh, current);
        Assert.False(current.IsCompleted);

        discovery.SetResult(Snapshot("COM1"));
        await current;

        Assert.Null(coordinator.CurrentTask);
        Assert.True(coordinator.WaitForCurrentRefreshAsync().IsCompletedSuccessfully);
    }

    [Fact]
    public async Task RequestMergedDuringFailedDiscoveryIsNotLost()
    {
        TaskCompletionSource<PortDiscoverySnapshot> firstDiscovery = NewDiscoverySource();
        int calls = 0;
        List<string> applied = [];
        List<Exception> errors = [];
        PortRefreshCoordinator coordinator = CreateCoordinator(
            () => ++calls == 1 ? firstDiscovery.Task : Task.FromResult(Snapshot("COM8")),
            snapshot => applied.Add(snapshot.Names.Single()), errors.Add);

        Task refresh = coordinator.RequestRefreshAsync();
        Assert.Same(refresh, coordinator.RequestRefreshAsync());
        firstDiscovery.SetException(new InvalidOperationException("device changed during enumeration"));
        await refresh;

        Assert.Equal(2, calls);
        Assert.Single(errors);
        Assert.Equal(["COM8"], applied);
        Assert.Null(coordinator.CurrentTask);
    }

    [Fact]
    public async Task RequestDuringApplyRunsAnotherPass()
    {
        PortRefreshCoordinator? coordinator = null;
        int applied = 0;
        int calls = 0;
        coordinator = CreateCoordinator(
            () => Task.FromResult(Snapshot($"COM{++calls}")),
            snapshot =>
            {
                if (++applied == 1) _ = coordinator!.RequestRefreshAsync();
            });

        await coordinator.RequestRefreshAsync();

        Assert.Equal(2, calls);
        Assert.Equal(2, applied);
    }

    [Fact]
    public async Task ThreeTimerRequestsDuringSlowDiscoveryCannotStarvePublication()
    {
        TaskCompletionSource<PortDiscoverySnapshot>[] passes = [NewDiscoverySource(), NewDiscoverySource(), NewDiscoverySource()];
        TaskCompletionSource[] started = [new(), new(), new()];
        int calls = 0;
        List<string> applied = [];
        PortRefreshCoordinator coordinator = CreateCoordinator(() =>
        {
            int index = calls++;
            started[index].SetResult();
            return passes[index].Task;
        }, snapshot => applied.Add(snapshot.Names.Single()));

        Task refresh = coordinator.RequestRefreshAsync();
        for (int index = 0; index < passes.Length; index++)
        {
            await started[index].Task;
            if (index < passes.Length - 1)
            {
                for (int tick = 0; tick < 3; tick++) Assert.Same(refresh, coordinator.RequestRefreshAsync());
            }
            passes[index].SetResult(Snapshot($"COM{index + 1}"));
        }
        await refresh;

        Assert.Equal(["COM1", "COM2", "COM3"], applied);
        Assert.Equal(3, calls);
        Assert.Null(coordinator.CurrentTask);
    }

    [Fact]
    public async Task ShutdownDuringDiscoveryDoesNotApplyOrRunTrailingPass()
    {
        TaskCompletionSource<PortDiscoverySnapshot> discovery = NewDiscoverySource();
        bool stopping = false;
        int calls = 0;
        int applied = 0;
        PortRefreshCoordinator coordinator = new(() => { calls++; return discovery.Task; }, _ => applied++,
            () => stopping, (_, _) => { }, _ => { }, DisabledSlowThreshold);
        Task refresh = coordinator.RequestRefreshAsync();
        _ = coordinator.RequestRefreshAsync();
        stopping = true;
        discovery.SetResult(Snapshot("COM8"));
        await refresh;
        Assert.Equal(1, calls);
        Assert.Equal(0, applied);
        Assert.Null(coordinator.CurrentTask);
    }

    private static PortRefreshCoordinator CreateCoordinator(
        Func<Task<PortDiscoverySnapshot>> discoverAsync,
        Action<PortDiscoverySnapshot>? apply = null,
        Action<Exception>? logException = null)
    {
        return new PortRefreshCoordinator(
            discoverAsync,
            apply ?? (_ => { }),
            () => false,
            (_, _) => { },
            logException ?? (_ => { }),
            DisabledSlowThreshold);
    }

    private static TaskCompletionSource<PortDiscoverySnapshot> NewDiscoverySource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static PortDiscoverySnapshot Snapshot(string portName) =>
        new([portName], new Dictionary<string, DiscoveredPort>(StringComparer.OrdinalIgnoreCase));
}
