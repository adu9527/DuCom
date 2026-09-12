using DuCom.Services;
using Xunit;

namespace DuCom.App.Tests;

public sealed class FramePacerTests
{
    [Fact]
    public void FirstFrameIsAllowed()
    {
        FramePacerDecision decision = FramePacer.Advance(TimeSpan.Zero, default);

        Assert.True(decision.ShouldRender);
        Assert.False(decision.ShouldRefreshStatus);
        Assert.Equal(FramePacer.MinimumRenderInterval, decision.State.NextRenderTime);
    }

    [Fact]
    public void FrameBeforeBoundaryIsSuppressedAndBoundaryIsAllowed()
    {
        FramePacerDecision first = FramePacer.Advance(TimeSpan.Zero, default);
        TimeSpan boundary = first.State.NextRenderTime;

        FramePacerDecision beforeBoundary = FramePacer.Advance(
            boundary - TimeSpan.FromTicks(1),
            first.State);
        FramePacerDecision atBoundary = FramePacer.Advance(boundary, first.State);

        Assert.False(beforeBoundary.ShouldRender);
        Assert.Equal(first.State, beforeBoundary.State);
        Assert.True(atBoundary.ShouldRender);
        Assert.Equal(boundary + FramePacer.MinimumRenderInterval, atBoundary.State.NextRenderTime);
    }

    [Fact]
    public void DroppedFrameSchedulesFromNowWhenIncrementCannotCatchUp()
    {
        FramePacerDecision first = FramePacer.Advance(TimeSpan.Zero, default);
        TimeSpan now = first.State.NextRenderTime + FramePacer.MinimumRenderInterval;

        FramePacerDecision dropped = FramePacer.Advance(now, first.State);

        Assert.True(dropped.ShouldRender);
        Assert.Equal(now + FramePacer.MinimumRenderInterval, dropped.State.NextRenderTime);
    }

    [Fact]
    public void StatusRefreshOccursAtBoundaryOnlyOnAllowedFrame()
    {
        FramePacerState state = new(
            NextRenderTime: TimeSpan.FromMilliseconds(150),
            LastStatusRefreshTime: TimeSpan.Zero);

        FramePacerDecision suppressed = FramePacer.Advance(TimeSpan.FromMilliseconds(100), state);
        FramePacerDecision allowed = FramePacer.Advance(TimeSpan.FromMilliseconds(150), suppressed.State);

        Assert.False(suppressed.ShouldRender);
        Assert.False(suppressed.ShouldRefreshStatus);
        Assert.Equal(TimeSpan.Zero, suppressed.State.LastStatusRefreshTime);
        Assert.True(allowed.ShouldRender);
        Assert.True(allowed.ShouldRefreshStatus);
        Assert.Equal(TimeSpan.FromMilliseconds(150), allowed.State.LastStatusRefreshTime);
    }

    [Fact]
    public void StatusRefreshIsGatedForAllowedFramesInsideOneHundredMilliseconds()
    {
        FramePacerState state = new(
            NextRenderTime: TimeSpan.FromMilliseconds(99),
            LastStatusRefreshTime: TimeSpan.Zero);

        FramePacerDecision beforeBoundary = FramePacer.Advance(TimeSpan.FromMilliseconds(99), state);
        FramePacerDecision atBoundary = FramePacer.Advance(
            FramePacer.StatusRefreshInterval,
            state with { NextRenderTime = FramePacer.StatusRefreshInterval });

        Assert.True(beforeBoundary.ShouldRender);
        Assert.False(beforeBoundary.ShouldRefreshStatus);
        Assert.Equal(TimeSpan.Zero, beforeBoundary.State.LastStatusRefreshTime);
        Assert.True(atBoundary.ShouldRender);
        Assert.True(atBoundary.ShouldRefreshStatus);
        Assert.Equal(FramePacer.StatusRefreshInterval, atBoundary.State.LastStatusRefreshTime);
    }
}
