namespace DuCom.Behaviors;

internal readonly record struct CoalescedActionRequest(bool ShouldSchedule, long Token);

internal sealed class CoalescedActionGate
{
    private readonly object _sync = new();
    private long _generation;
    private bool _isPending;

    public CoalescedActionRequest Request()
    {
        lock (_sync)
        {
            if (_isPending)
            {
                return default;
            }

            _isPending = true;
            return new CoalescedActionRequest(true, ++_generation);
        }
    }

    public bool TryBeginExecution(long token)
    {
        lock (_sync)
        {
            return _isPending && token == _generation;
        }
    }

    public void Complete(long token)
    {
        lock (_sync)
        {
            if (token == _generation)
            {
                _isPending = false;
            }
        }
    }

    public void Cancel()
    {
        lock (_sync)
        {
            _isPending = false;
            _generation++;
        }
    }
}
