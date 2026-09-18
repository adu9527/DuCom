using System.Windows;
using DuCom.Core.Search;
using DuCom.Core.Storage;

namespace DuCom.Controls;

public sealed partial class BoundedLogEditor
{
    public event EventHandler? SearchSnapshotChanged;
    private SearchMatch? _appliedMatch;
    private int _searchSelectionStart = -1;
    private int _searchSelectionLength;
    private bool _applyingSearchSelection;

    private static void OnCurrentMatchChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        BoundedLogEditor editor = (BoundedLogEditor)d;
        if (editor._appliedMatch is not null)
        {
            editor.ClearSearchSelectionIfOwned();
        }
        editor._appliedMatch = null;
        editor.ApplyCurrentMatch();
    }

    private static void OnSearchMatchesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        BoundedLogEditor editor = (BoundedLogEditor)d;
        editor.UpdateSearchHighlights();
    }

    private void ApplyCurrentMatch()
    {
        if (CurrentMatch is not SearchMatch match || _appliedMatch == match)
        {
            return;
        }
        ProjectedLine? line = _projected.FirstOrDefault(item =>
            item.LogicalId == match.LogicalId && item.SegmentIndex == match.SegmentIndex);
        if (line is null)
        {
            return;
        }

        CancelViewportRestore();
        _pendingFollowRender?.Abort();
        _pendingFollowRender = null;

        int relativeStart = Math.Clamp(match.StartIndex, 0, line.Text.Length);
        int start = checked((int)(line.StartOffset - _documentOriginOffset + relativeStart));
        int length = Math.Clamp(match.Length, 0, line.Text.Length - relativeStart);
        _applyingSearchSelection = true;
        try
        {
            Select(start, length);
            TextArea.Caret.Offset = start;
            ScrollToLine(Document.GetLineByOffset(start).LineNumber);
            _appliedMatch = match;
            _searchSelectionStart = start;
            _searchSelectionLength = length;
        }
        finally
        {
            _applyingSearchSelection = false;
        }
    }

    internal LineStoreSnapshot CreateSearchSnapshot()
    {
        StoredLine[] lines = [.. _projected.Select(line => new StoredLine(
            line.LogicalId,
            line.SegmentIndex,
            line.Source.Direction,
            line.Source.TimestampUtc,
            line.Text,
            true))];
        return new LineStoreSnapshot(
            lines.Length == 0 ? null : lines[0].LogicalId,
            lines.Length == 0 ? null : lines[^1].LogicalId,
            0,
            lines);
    }

    internal LineCursor? GetSearchNavigationAnchor()
    {
        ViewportAnchor? anchor = CaptureViewportAnchor();
        return anchor is null ? null : new LineCursor(anchor.LogicalId, anchor.SegmentIndex);
    }

    private void NotifySearchSnapshotChanged() => SearchSnapshotChanged?.Invoke(this, EventArgs.Empty);

    private void UpdateSearchHighlights()
    {
        List<SearchColorSpan> spans = [];
        foreach (SearchMatch match in SearchMatches ?? [])
        {
            if (match.Length <= 0)
            {
                continue;
            }
            ProjectedLine? line = _projected.FirstOrDefault(item =>
                item.LogicalId == match.LogicalId && item.SegmentIndex == match.SegmentIndex);
            if (line is null)
            {
                continue;
            }
            int relativeStart = Math.Clamp(match.StartIndex, 0, line.Text.Length);
            int length = Math.Clamp(match.Length, 0, line.Text.Length - relativeStart);
            if (length > 0)
            {
                spans.Add(new SearchColorSpan(line.StartOffset + relativeStart, length));
            }
        }
        _colorizer.SetSearchSpans(spans);
        TextArea.TextView.Redraw();
    }

    private void OnSelectionChanged(object? sender, EventArgs e)
    {
        if (!_applyingSearchSelection &&
            (SelectionStart != _searchSelectionStart || SelectionLength != _searchSelectionLength))
        {
            _appliedMatch = null;
            _searchSelectionStart = -1;
            _searchSelectionLength = 0;
        }
    }

    private void ClearSearchSelectionIfOwned()
    {
        if (SelectionStart == _searchSelectionStart && SelectionLength == _searchSelectionLength)
        {
            Select(SelectionStart, 0);
        }
        _searchSelectionStart = -1;
        _searchSelectionLength = 0;
    }
}
