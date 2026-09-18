namespace DuCom.Services;

internal readonly record struct FramePacerState(
    TimeSpan NextRenderTime,
    TimeSpan LastStatusRefreshTime);

internal readonly record struct FramePacerDecision(
    FramePacerState State,
    bool ShouldRender,
    bool ShouldRefreshStatus);

/// <summary>Pure frame and status pacing state transition for UI rendering.</summary>
internal static class FramePacer
{
    internal static readonly TimeSpan MinimumRenderInterval = TimeSpan.FromSeconds(1d / 85d);
    internal static readonly TimeSpan StatusRefreshInterval = TimeSpan.FromMilliseconds(100);

    internal static TimeSpan GetProjectionBudget(int uniqueSessionCount) => uniqueSessionCount switch
    {
        <= 0 => TimeSpan.Zero,
        1 => TimeSpan.FromMilliseconds(8),
        _ => TimeSpan.FromMilliseconds(4),
    };

    internal static FramePacerDecision Advance(TimeSpan now, FramePacerState state)
    {
        if (now < state.NextRenderTime)
        {
            return new FramePacerDecision(state, ShouldRender: false, ShouldRefreshStatus: false);
        }

        TimeSpan nextRenderTime = state.NextRenderTime + MinimumRenderInterval;
        if (nextRenderTime <= now)
        {
            nextRenderTime = now + MinimumRenderInterval;
        }

        bool shouldRefreshStatus = now - state.LastStatusRefreshTime >= StatusRefreshInterval;
        return new FramePacerDecision(
            new FramePacerState(
                nextRenderTime,
                shouldRefreshStatus ? now : state.LastStatusRefreshTime),
            ShouldRender: true,
            shouldRefreshStatus);
    }
}
