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
    public async Task NewRequestAfterDiscoveryStartedDiscardsOldResult()
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

        Assert.Equal(["COM2"], appliedPorts);
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
