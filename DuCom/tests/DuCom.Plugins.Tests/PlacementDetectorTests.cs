using DuCom.Plugins.Production;
using Xunit;

namespace DuCom.Plugins.Tests;

public sealed class PlacementDetectorTests
{
    [Fact]
    public void IdentityChangeAloneNeverTriggersAndSamePairCanBeReinserted()
    {
        PlacementDetector d = new();
        Assert.False(d.Observe("A/B/", false)); Assert.True(d.Observe("A/B/", false));
        for (int i = 0; i < 5; i++) Assert.False(d.Observe("C/B/", false));
        for (int i = 0; i < 5; i++) Assert.False(d.Observe("C/D/", false));
        for (int i = 0; i < 4; i++) Assert.False(d.Observe(null, true));
        Assert.False(d.WaitingForRemoval);
        Assert.False(d.Observe("A/B/", false)); Assert.True(d.Observe("A/B/", false));
        for (int i = 0; i < 5; i++) Assert.False(d.Observe("A/B/", false));
    }
    [Fact]
    public void StablePairRunsOnceAndRequiresFullRemovalBeforeNextPair()
    {
        PlacementDetector d = new();
        Assert.False(d.Observe(null, true)); Assert.False(d.Observe("A/B", false)); Assert.True(d.Observe("A/B", false));
        Assert.False(d.Observe("A/B", false)); Assert.False(d.Observe("C/D", false));
        Assert.False(d.ObserveRemoval(0, true, [0, 1]));
        Assert.False(d.ObserveRemoval(1, false, [0, 1]));
        Assert.True(d.WaitingForRemoval);
        Assert.True(d.ObserveRemoval(1, true, [0, 1]));
        Assert.False(d.WaitingForRemoval);
        Assert.False(d.Observe("C/D", false)); Assert.True(d.Observe("C/D", false));
    }
}
