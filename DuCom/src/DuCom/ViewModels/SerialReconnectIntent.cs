namespace DuCom.ViewModels;

internal sealed class SerialReconnectIntent
{
    private bool _wasOpened;

    public bool Waiting { get; private set; }

    public bool ShouldHandleRemoval(bool isOpen) => !Waiting && (isOpen || _wasOpened);

    public void Opened()
    {
        _wasOpened = true;
        Waiting = false;
    }

    public void Removed(bool enabled)
    {
        Waiting = _wasOpened && enabled;
        if (!Waiting) _wasOpened = false;
    }

    public void RetryPending() => Waiting = true;

    public void Cancel()
    {
        _wasOpened = false;
        Waiting = false;
    }
}
