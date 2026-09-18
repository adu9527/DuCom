using DuCom.Core.Storage;

namespace DuCom.Core.Sessions;

public sealed partial class SerialSession
{
    public LineStoreSnapshot GetLinesAfter(LineCursor? cursor, int maximumSegments = 2_048) =>
        _lineStore.SnapshotAfter(cursor, maximumSegments);

    public bool HasPendingLinesOverLimit(LineCursor? cursor, int maximumSegments, int maximumCharacters) =>
        _lineStore.HasPendingDataOverLimit(cursor, maximumSegments, maximumCharacters);

    public LineStoreSnapshot GetLatestLines(int maximumSegments, int maximumCharacters) =>
        _lineStore.SnapshotTail(maximumSegments, maximumCharacters);

    public void ClearDisplay() => _lineStore.Clear();

    public void SetMemoryPressure(bool active)
    {
        _lineStore.SetMemoryPressure(active);
        if (active)
        {
            _rawTaps.TrimForMemoryPressure();
        }
    }

    /// <summary>Display tap fan-out for auxiliary surfaces (float send window, log filter).</summary>
    public SessionTapHub DisplayTaps => _displayTaps;

    /// <summary>Raw RX/TX traffic hub, including the legacy RX-only tap surface.</summary>
    public SessionRawTapHub RawTaps => _rawTaps;

    /// <summary>Stable identity of this session instance across close and reopen.</summary>
    public string RuntimeId { get; } = Guid.NewGuid().ToString("N");

    /// <summary>Generation of the currently open transport runtime, or null while closed.</summary>
    public Guid? RuntimeGeneration => _rawTaps.Generation;
}
