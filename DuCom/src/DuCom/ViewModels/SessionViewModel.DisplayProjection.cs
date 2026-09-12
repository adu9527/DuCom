using System.Globalization;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DuCom.Core.Parsing;
using DuCom.Core.Sessions;
using DuCom.Core.Storage;

namespace DuCom.ViewModels;

public partial class SessionViewModel
{
    private readonly AnsiDisplayProjector _projector = new();
    private long? _renderedLastLogicalId;
    private int _renderedLastSegmentIndex = -1;
    private const int MaximumVisibleSegments = 10_000;
    private const int MaximumVisibleCharacters = 1_400_000;
    private const int MaximumSegmentsPerRender = 128;
    private LineStoreSnapshot _visibleSearchSnapshot = new(null, null, 0, []);
    private bool _visibleSearchSnapshotDirty = true;
    private long _nextSearchSnapshotTimestamp;
    private int _visibleCharacterCount;
    private const char EscapeCharacter = '\u001B';

    public BatchObservableCollection<LogLineViewModel> VisibleLines { get; } = [];

    public SearchViewModel Search { get; } = new();

    [ObservableProperty]
    public partial bool FollowEnd { get; set; } = true;

    [ObservableProperty]
    public partial long EvictedLineCount { get; private set; }

    public bool HasEvictions => EvictedLineCount > 0;

    public string EvictionDisplay =>
        GetResourceString("Log.Evicted")
            .Replace("{0}", EvictedLineCount.ToString(CultureInfo.CurrentCulture), StringComparison.Ordinal);

    internal LineStoreSnapshot GetVisibleSearchSnapshot() => Volatile.Read(ref _visibleSearchSnapshot);

    private void UpdateVisibleSearchSnapshot()
    {
        StoredLine[] lines = [.. VisibleLines.Select(line => new StoredLine(
            line.LogicalId,
            line.SegmentIndex,
            line.Direction,
            line.TimestampUtc,
            line.Text,
            true))];
        Volatile.Write(ref _visibleSearchSnapshot, new LineStoreSnapshot(
            lines.Length == 0 ? null : lines[0].LogicalId,
            lines.Length == 0 ? null : lines[^1].LogicalId,
            EvictedLineCount,
            lines));
        _visibleSearchSnapshotDirty = false;
    }

    public bool PullDisplaySnapshot(bool publishSearchSnapshot = false)
    {
        bool stateChanged = RefreshState();
        LineCursor? cursor = _renderedLastLogicalId.HasValue
            ? new LineCursor(_renderedLastLogicalId.Value, _renderedLastSegmentIndex)
            : null;
        // Keep each workspace's projection batch small enough for a stable UI frame.
        // Split panes call this independently and never share a quota or cursor.
        LineStoreSnapshot snapshot = _session.GetDisplaySnapshot(cursor, MaximumSegmentsPerRender);
        if (EvictedLineCount != snapshot.EvictedLineCount)
        {
            EvictedLineCount = snapshot.EvictedLineCount;
            OnPropertyChanged(nameof(HasEvictions));
            OnPropertyChanged(nameof(EvictionDisplay));
        }

        if (snapshot.FirstLogicalId is null)
        {
            if (VisibleLines.Count > 0)
            {
                VisibleLines.Clear();
                _visibleCharacterCount = 0;
                _visibleSearchSnapshotDirty = true;
            }
            _projector.Reset();
            _renderedLastLogicalId = null;
            _renderedLastSegmentIndex = -1;
            if (publishSearchSnapshot && ShouldPublishSearchSnapshot())
            {
                UpdateVisibleSearchSnapshot();
            }
            return stateChanged;
        }

        using IDisposable update = VisibleLines.BeginUpdate();
        int evictedPrefixCount = 0;
        while (evictedPrefixCount < VisibleLines.Count &&
               VisibleLines[evictedPrefixCount].LogicalId < snapshot.FirstLogicalId.Value)
        {
            _visibleCharacterCount -= GetDisplayCharacterCount(VisibleLines[evictedPrefixCount]);
            evictedPrefixCount++;
        }
        if (evictedPrefixCount > 0)
        {
            VisibleLines.RemoveFirst(evictedPrefixCount);
            _visibleSearchSnapshotDirty = true;
        }

        IReadOnlyList<HighlightFilterRule> effectiveRules = FilterEnabled
            ? HighlightFilterRules
            : HighlightFilterRules.Where(rule => rule.Kind != HighlightFilterRuleKind.Filter).ToArray();
        foreach (StoredLine line in snapshot.Lines)
        {
            if (_renderedLastLogicalId is not null &&
                (line.LogicalId < _renderedLastLogicalId ||
                 line.LogicalId == _renderedLastLogicalId && line.SegmentIndex <= _renderedLastSegmentIndex))
            {
                continue;
            }

            // Commit the projection cursor before visibility filtering so hidden lines are
            // never re-delivered by later snapshots.
            AnsiProjection projection = _projector.Project(line.Text, effectiveRules);
            if (projection.HasRegexTimeout)
            {
                ReportRegexTimeout();
            }

            if (!projection.IsVisible)
            {
                _renderedLastLogicalId = line.LogicalId;
                _renderedLastSegmentIndex = line.SegmentIndex;
                continue;
            }

            if (VisibleLines.Count > 0 &&
                VisibleLines[^1].LogicalId == line.LogicalId &&
                VisibleLines[^1].Text.Length + projection.DisplayText.Length <= 4_096)
            {
                LogLineViewModel previous = VisibleLines[^1];
                LogLineViewModel replacement = previous with
                {
                    SegmentIndex = line.SegmentIndex,
                    Text = previous.Text + projection.DisplayText,
                    StyledRuns = ConcatenateRuns(previous.StyledRuns, projection.Runs),
                };
                VisibleLines[^1] = replacement;
                _visibleCharacterCount += replacement.Text.Length - previous.Text.Length;
            }
            else
            {
                LogLineViewModel visibleLine = new(
                    line.LogicalId,
                    line.SegmentIndex,
                    line.TimestampUtc,
                    line.Direction,
                    projection.DisplayText,
                    projection.Runs);
                VisibleLines.Add(visibleLine);
                _visibleCharacterCount += GetDisplayCharacterCount(visibleLine);
            }
            _visibleSearchSnapshotDirty = true;
            _renderedLastLogicalId = line.LogicalId;
            _renderedLastSegmentIndex = line.SegmentIndex;
        }

        int trimCount = 0;
        while (VisibleLines.Count - trimCount > MaximumVisibleSegments ||
               _visibleCharacterCount > MaximumVisibleCharacters && VisibleLines.Count - trimCount > 1)
        {
            _visibleCharacterCount -= GetDisplayCharacterCount(VisibleLines[trimCount]);
            trimCount++;
        }
        if (trimCount > 0)
        {
            VisibleLines.RemoveFirst(trimCount);
            _visibleSearchSnapshotDirty = true;
        }
        if (publishSearchSnapshot && ShouldPublishSearchSnapshot())
        {
            UpdateVisibleSearchSnapshot();
        }
        return stateChanged;
    }

    private bool ShouldPublishSearchSnapshot()
    {
        if (!_visibleSearchSnapshotDirty)
        {
            return false;
        }

        long now = Stopwatch.GetTimestamp();
        if (now < _nextSearchSnapshotTimestamp)
        {
            return false;
        }

        _nextSearchSnapshotTimestamp = now + Stopwatch.Frequency / 5;
        return true;
    }

    [RelayCommand]
    public void ClearDisplay()
    {
        _session.ClearDisplay();
        VisibleLines.Clear();
        _visibleCharacterCount = 0;
        _visibleSearchSnapshotDirty = true;
        _projector.Reset();
        _renderedLastLogicalId = null;
        _renderedLastSegmentIndex = -1;
        UpdateVisibleSearchSnapshot();
    }

    private static IReadOnlyList<StyleRun> ConcatenateRuns(
        IReadOnlyList<StyleRun> first,
        IReadOnlyList<StyleRun> second)
    {
        if (second.Count == 0)
        {
            return first;
        }

        if (first.Count == 0)
        {
            return second;
        }

        List<StyleRun> combined = new(first.Count + second.Count);
        combined.AddRange(first);
        combined.AddRange(second);
        return combined;
    }

    private static int GetDisplayCharacterCount(LogLineViewModel line) =>
        line.Text.Length + Environment.NewLine.Length;
}
