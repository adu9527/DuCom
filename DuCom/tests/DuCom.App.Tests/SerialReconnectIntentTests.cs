using DuCom.ViewModels;
using Xunit;

namespace DuCom.App.Tests;

public sealed class SerialReconnectIntentTests
{
    [Fact]
    public void FaultClosedSessionReconnectsOnlyAfterPreviouslyOpeningAndDeviceReturning()
    {
        SerialReconnectIntent intent = new();
        Assert.False(intent.ShouldHandleRemoval(isOpen: false));

        intent.Opened();
        Assert.True(intent.ShouldHandleRemoval(isOpen: false));
        intent.Removed(enabled: true);
        Assert.True(intent.Waiting);
        Assert.False(intent.ShouldHandleRemoval(isOpen: false));

        intent.Opened();
        Assert.False(intent.Waiting);
    }

    [Fact]
    public void ExplicitCloseCancelsReconnectButFailedAttemptCanKeepWaiting()
    {
        SerialReconnectIntent intent = new();
        intent.Opened();
        intent.Removed(enabled: true);
        intent.RetryPending();
        Assert.True(intent.Waiting);

        intent.Cancel();
        Assert.False(intent.Waiting);
        Assert.False(intent.ShouldHandleRemoval(isOpen: false));
    }
}
