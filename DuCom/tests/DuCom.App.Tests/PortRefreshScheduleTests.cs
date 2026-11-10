using DuCom.Services;
using Xunit;

namespace DuCom.App.Tests;

public sealed class PortRefreshScheduleTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.UnixEpoch;

    [Fact]
    public void NoNotificationsStillRefreshesPresencePeriodically()
    {
        PortRefreshSchedule schedule = new(Start);
        Assert.False(schedule.ShouldRefresh(Start + TimeSpan.FromSeconds(1)));
        Assert.True(schedule.ShouldRefresh(Start + TimeSpan.FromSeconds(2)));
        Assert.False(schedule.ShouldRefresh(Start + TimeSpan.FromSeconds(3)));
        Assert.True(schedule.ShouldRefresh(Start + TimeSpan.FromSeconds(4)));
    }

    [Fact]
    public void BurstDebouncesButContinuousDbtStormCannotPostponeRefreshForever()
    {
        PortRefreshSchedule schedule = new(Start);
        for (int milliseconds = 0; milliseconds < 1000; milliseconds += 200)
        {
            DateTimeOffset now = Start + TimeSpan.FromMilliseconds(milliseconds);
            schedule.Notify(now);
            Assert.False(schedule.ShouldRefresh(now));
        }
        schedule.Notify(Start + TimeSpan.FromSeconds(1));
        Assert.True(schedule.ShouldRefresh(Start + TimeSpan.FromSeconds(1)));
        for (int milliseconds = 1200; milliseconds < 2000; milliseconds += 200)
        {
            DateTimeOffset now = Start + TimeSpan.FromMilliseconds(milliseconds);
            schedule.Notify(now);
            Assert.False(schedule.ShouldRefresh(now));
        }
        Assert.True(schedule.ShouldRefresh(Start + TimeSpan.FromMilliseconds(2000)));
    }

    [Fact]
    public void SingleNotificationHasTrailingSettlingAndReturnsToFallback()
    {
        PortRefreshSchedule schedule = new(Start);
        schedule.Notify(Start);
        Assert.False(schedule.ShouldRefresh(Start + TimeSpan.FromMilliseconds(200)));
        Assert.True(schedule.ShouldRefresh(Start + TimeSpan.FromMilliseconds(400)));
        Assert.True(schedule.ShouldRefresh(Start + TimeSpan.FromMilliseconds(1400)));
        Assert.True(schedule.ShouldRefresh(Start + TimeSpan.FromMilliseconds(2400)));
        Assert.False(schedule.ShouldRefresh(Start + TimeSpan.FromMilliseconds(3400)));
        Assert.True(schedule.ShouldRefresh(Start + TimeSpan.FromMilliseconds(4400)));
    }
}
