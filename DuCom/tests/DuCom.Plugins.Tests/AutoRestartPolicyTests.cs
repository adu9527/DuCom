using DuCom.PluginHost;
using Xunit;

namespace DuCom.Plugins.Tests;

public sealed class AutoRestartPolicyTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FirstFaultSchedulesBaseDelay()
    {
        AutoRestartPolicy.Decision decision = AutoRestartPolicy.OnFault(null, null, T0);
        Assert.True(decision.Schedule);
        Assert.Equal(1, decision.Attempt);
        Assert.Equal(AutoRestartPolicy.BaseDelay, decision.Delay);
        Assert.Null(decision.SkipReason);
    }

    [Theory]
    [InlineData(1, 4)]
    [InlineData(2, 8)]
    public void RapidCrashStreakDoublesDelay(int previousCrashes, int expectedDelaySeconds)
    {
        AutoRestartPolicy.Decision decision = AutoRestartPolicy.OnFault(
            previousCrashes, lastHealthyStartUtc: null, faultUtc: T0);
        Assert.True(decision.Schedule);
        Assert.Equal(previousCrashes + 1, decision.Attempt);
        Assert.Equal(TimeSpan.FromSeconds(expectedDelaySeconds), decision.Delay);
    }

    [Fact]
    public void BackoffDelayIsCappedAtMaximum()
    {
        Assert.Equal(TimeSpan.FromSeconds(16), AutoRestartPolicy.DelayFor(4));
        Assert.Equal(AutoRestartPolicy.MaxDelay, AutoRestartPolicy.DelayFor(10));
    }

    [Fact]
    public void StreakGivesUpPastMaximum()
    {
        AutoRestartPolicy.Decision decision = AutoRestartPolicy.OnFault(
            AutoRestartPolicy.MaxConsecutiveRestarts, null, T0);
        Assert.False(decision.Schedule);
        Assert.Equal(AutoRestartPolicy.MaxConsecutiveRestarts + 1, decision.Attempt);
        Assert.Contains("manual retry", decision.SkipReason);
    }

    [Fact]
    public void CrashAfterStableRunResetsStreak()
    {
        // The plugin started healthy six minutes before crashing: a fresh streak.
        DateTimeOffset healthyStart = T0 - AutoRestartPolicy.StableWindow - TimeSpan.FromMinutes(1);
        AutoRestartPolicy.Decision decision = AutoRestartPolicy.OnFault(
            AutoRestartPolicy.MaxConsecutiveRestarts, healthyStart, T0);
        Assert.True(decision.Schedule);
        Assert.Equal(1, decision.Attempt);
        Assert.Equal(AutoRestartPolicy.BaseDelay, decision.Delay);
    }

    [Fact]
    public void CrashJustAfterStartExtendsStreak()
    {
        // Restart went live, then crashed 30 seconds later: the loop guard applies.
        DateTimeOffset healthyStart = T0 - TimeSpan.FromSeconds(30);
        AutoRestartPolicy.Decision decision = AutoRestartPolicy.OnFault(1, healthyStart, T0);
        Assert.True(decision.Schedule);
        Assert.Equal(2, decision.Attempt);
    }

    [Fact]
    public void ExactlyStableWindowResetsStreak()
    {
        DateTimeOffset healthyStart = T0 - AutoRestartPolicy.StableWindow;
        AutoRestartPolicy.Decision decision = AutoRestartPolicy.OnFault(1, healthyStart, T0);
        Assert.Equal(1, decision.Attempt);
    }
}
