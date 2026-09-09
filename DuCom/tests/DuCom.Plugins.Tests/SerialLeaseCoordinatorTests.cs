using DuCom.PluginHost.Core;
using Xunit;

namespace DuCom.Plugins.Tests;

public sealed class SerialLeaseCoordinatorTests
{
    [Fact]
    public void AcquireIsAtomicAndPortNamesAreNormalized()
    {
        SerialLeaseCoordinator coordinator = new();
        SerialLeaseSnapshot first = coordinator.Acquire("plugin-a", "activation-a", "task-a", " com7 ", null, true, false);
        Assert.Equal("COM7", first.Port);
        Assert.False(coordinator.CanUse("COM7"));
        Assert.Throws<InvalidOperationException>(() => coordinator.Acquire("plugin-b", "activation-b", "task-b", "COM7", null, false, false));
    }

    [Fact]
    public async Task OnlyOwnerScopeCanUseLeasedPort()
    {
        SerialLeaseCoordinator coordinator = new();
        SerialLeaseSnapshot lease = coordinator.Acquire("plugin-a", "activation-a", "task-a", "COM9", null, true, true);
        Assert.False(coordinator.CanUse("com9"));
        bool inside = false;
        await coordinator.RunAsOwnerAsync(lease.LeaseId, () =>
        {
            inside = coordinator.CanUse("COM9");
            return Task.CompletedTask;
        });
        Assert.True(inside);
        Assert.False(coordinator.CanUse("COM9"));
    }

    [Fact]
    public void LeaseIsBoundToPluginAndActivation()
    {
        SerialLeaseCoordinator coordinator = new();
        SerialLeaseSnapshot lease = coordinator.Acquire("plugin-a", "activation-a", "task-a", "COM4", "device-1", false, false);
        Assert.Throws<InvalidOperationException>(() => coordinator.GetOwned("plugin-b", "activation-a", lease.LeaseId));
        Assert.Throws<InvalidOperationException>(() => coordinator.GetOwned("plugin-a", "activation-b", lease.LeaseId));
        Assert.Equal(lease, coordinator.GetOwned("plugin-a", "activation-a", lease.LeaseId));
    }

    [Fact]
    public void ReleaseMakesPortAvailableAgain()
    {
        SerialLeaseCoordinator coordinator = new();
        SerialLeaseSnapshot lease = coordinator.Acquire("plugin", "activation", "task", "COM2", null, false, false);
        coordinator.Release(lease.LeaseId);
        Assert.True(coordinator.CanUse("COM2"));
        coordinator.Acquire("other", "next", "task-next", "COM2", null, false, false);
    }

    [Fact]
    public void RevokeReleasesOnlyMatchingActivation()
    {
        SerialLeaseCoordinator coordinator = new();
        coordinator.Acquire("plugin", "activation-a", "task-a", "COM2", null, false, false);
        coordinator.Acquire("plugin", "activation-b", "task-b", "COM3", null, false, false);
        coordinator.Revoke("plugin", "activation-a");
        Assert.True(coordinator.CanUse("COM2"));
        Assert.False(coordinator.CanUse("COM3"));
    }

    [Fact]
    public async Task PortOperationAndAcquireShareOneAtomicGate()
    {
        SerialLeaseCoordinator coordinator = new();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task operation = coordinator.RunPortOperationAsync("COM8", async () => { entered.SetResult(); await release.Task; return true; });
        await entered.Task;
        bool acquired = false;
        Task acquire = coordinator.RunPortOperationAsync("COM8", () => { coordinator.Acquire("p", "a", "t", "COM8", null, false, false); acquired = true; return true; });
        await Task.Delay(50);
        Assert.False(acquired);
        release.SetResult();
        await Task.WhenAll(operation, acquire);
        Assert.True(acquired);
    }
}
