namespace DuCom.Services;

internal sealed class SettingsSaveGate
{
    private bool _isDirty;

    internal bool IsDirty => _isDirty;

    internal void MarkDirty() => _isDirty = true;

    internal bool TryConsumeDirty()
    {
        if (!_isDirty)
        {
            return false;
        }

        _isDirty = false;
        return true;
    }
}
