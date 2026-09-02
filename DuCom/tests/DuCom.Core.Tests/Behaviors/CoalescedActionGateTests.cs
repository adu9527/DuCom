using DuCom.Behaviors;

namespace DuCom.Core.Tests.Behaviors;

public sealed class CoalescedActionGateTests
{
    [Fact]
    public void RepeatedRequestsScheduleOnlyOneActionUntilCompletion()
    {
        CoalescedActionGate gate = new();

        CoalescedActionRequest first = gate.Request();
        CoalescedActionRequest second = gate.Request();

        Assert.True(first.ShouldSchedule);
        Assert.False(second.ShouldSchedule);
        Assert.True(gate.TryBeginExecution(first.Token));

        gate.Complete(first.Token);

        Assert.True(gate.Request().ShouldSchedule);
    }

    [Fact]
    public void CancelInvalidatesPendingActionImmediately()
    {
        CoalescedActionGate gate = new();
        CoalescedActionRequest request = gate.Request();

        gate.Cancel();

        Assert.False(gate.TryBeginExecution(request.Token));
        Assert.True(gate.Request().ShouldSchedule);
    }

    [Fact]
    public void StaleCompletionDoesNotClearReplacementRequest()
    {
        CoalescedActionGate gate = new();
        CoalescedActionRequest stale = gate.Request();
        gate.Cancel();
        CoalescedActionRequest replacement = gate.Request();

        gate.Complete(stale.Token);

        Assert.False(gate.Request().ShouldSchedule);
        Assert.True(gate.TryBeginExecution(replacement.Token));
    }

    [Fact]
    public void RequestsDuringExecutionRemainCoalesced()
    {
        CoalescedActionGate gate = new();
        CoalescedActionRequest request = gate.Request();

        Assert.True(gate.TryBeginExecution(request.Token));
        Assert.False(gate.Request().ShouldSchedule);

        gate.Complete(request.Token);

        Assert.True(gate.Request().ShouldSchedule);
    }
}
