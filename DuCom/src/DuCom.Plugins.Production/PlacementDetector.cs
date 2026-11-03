namespace DuCom.Plugins.Production;

// Only a confirmed removal rearms the fixture. Device identity is used to
// debounce insertion, never to prohibit testing the same pair again.
public sealed class PlacementDetector
{
    public bool WaitingForRemoval { get; private set; }
    private string? _identity;
    private int _stable;
    private readonly HashSet<int> _removedChannels = [];
    public bool ObserveRemoval(int channel, bool timedOut, IReadOnlyList<int> selectedChannels)
    {
        if (!WaitingForRemoval) return false;
        if (timedOut) _removedChannels.Add(channel);
        if (!selectedChannels.All(_removedChannels.Contains)) return false;
        WaitingForRemoval = false; _identity = null; _stable = 0; _removedChannels.Clear();
        return true;
    }
    public bool Observe(string? identity, bool allAbsent)
    {
        if (WaitingForRemoval)
        {
            if (allAbsent) { WaitingForRemoval = false; _identity = null; _stable = 0; _removedChannels.Clear(); }
            return false;
        }
        _stable = identity is not null && identity == _identity ? _stable + 1 : identity is null ? 0 : 1;
        _identity = identity;
        if (_stable < 2) return false;
        WaitingForRemoval = true;
        _removedChannels.Clear();
        return true;
    }
}
