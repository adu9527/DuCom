using System.Windows;
using DuCom.Core.Search;

namespace DuCom.Controls;

public sealed partial class BoundedLogEditor
{
    private SearchMatch? _appliedMatch;
    private int _searchSelectionStart = -1;
    private int _searchSelectionLength;
    private bool _applyingSearchSelection;

    private static void OnCurrentMatchChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        BoundedLogEditor editor = (BoundedLogEditor)d;
        if (e.NewValue is null && editor._appliedMatch is not null)
        {
            editor.ClearSearchSelectionIfOwned();
        }
        editor._appliedMatch = null;
        editor.ApplyCurrentMatch();
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

        int relativeStart = Math.Clamp(match.StartIndex, 0, line.Text.Length);
        int start = line.StartOffset + relativeStart;
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
