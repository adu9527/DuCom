using DuCom.Core.Storage;

namespace DuCom.Core.Sessions;

public sealed partial class SerialSession
{
    public LineStoreSnapshot GetLinesAfter(LineCursor? cursor, int maximumSegments = 2_048) =>
        _lineStore.SnapshotAfter(cursor, maximumSegments);

    public void ClearDisplay() => _lineStore.Clear();

    /// <summary>Display tap fan-out for auxiliary surfaces (float send window, log filter).</summary>
    public SessionTapHub DisplayTaps => _displayTaps;

    /// <summary>Raw pre-formatting receive observers (host-internal broker surface).</summary>
    public SessionRawTapHub RawTaps => _rawTaps;

    /// <summary>Stable identity of this session instance; a reopen creates a new runtime id.</summary>
    public string RuntimeId { get; } = Guid.NewGuid().ToString("N");
}
