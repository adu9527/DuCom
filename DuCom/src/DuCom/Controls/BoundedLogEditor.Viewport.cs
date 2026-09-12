using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Document;

namespace DuCom.Controls;

public sealed partial class BoundedLogEditor
{
    private DispatcherOperation? _pendingViewportRestore;
    private ViewportAnchor? _viewportAnchor;

    private void SetVerticalScrollThumbMinimum()
    {
        foreach (ScrollBar scrollBar in FindVisualDescendants<ScrollBar>(this))
        {
            if (scrollBar.Orientation != Orientation.Vertical)
            {
                continue;
            }

            if (TryFindResource("Style.LogVerticalScrollBar") is Style style && !ReferenceEquals(scrollBar.Style, style))
            {
                scrollBar.Style = style;
            }
        }
    }

    private static IEnumerable<T> FindVisualDescendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (T descendant in FindVisualDescendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private ViewportAnchor? CaptureViewportAnchor()
    {
        if (FollowEnd && !_followSuppressed || _projected.Count == 0 || Document.TextLength == 0)
        {
            return null;
        }

        try
        {
            TextArea.TextView.EnsureVisualLines();
            DocumentLine documentLine = TextArea.TextView.GetDocumentLineByVisualTop(VerticalOffset);
            ProjectedLine? projected = _projected.FirstOrDefault(line =>
                line.StartOffset <= documentLine.Offset && documentLine.Offset < line.EndOffset);
            if (projected is null)
            {
                return null;
            }

            double lineTop = TextArea.TextView.GetVisualTopByDocumentLine(documentLine.LineNumber);
            return new ViewportAnchor(
                projected.LogicalId,
                projected.SegmentIndex,
                Math.Max(0d, VerticalOffset - lineTop));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void RestoreViewport(ViewportAnchor? anchor)
    {
        if (anchor is null || _projected.Count == 0 || Document.TextLength == 0)
        {
            return;
        }

        ProjectedLine? projected = _projected.FirstOrDefault(line =>
            line.LogicalId == anchor.LogicalId && line.SegmentIndex == anchor.SegmentIndex)
            ?? _projected[0];
        DocumentLine documentLine = Document.GetLineByOffset(Math.Min(projected.StartOffset, Document.TextLength));
        double lineTop = TextArea.TextView.GetVisualTopByDocumentLine(documentLine.LineNumber);
        ScrollToVerticalOffset(Math.Max(0d, lineTop + anchor.OffsetWithinLine));
    }

    private void ScheduleViewportRestore(ViewportAnchor? anchor)
    {
        if (anchor is null)
        {
            return;
        }

        _viewportAnchor = anchor;
        if (_pendingViewportRestore is { Status: DispatcherOperationStatus.Pending })
        {
            return;
        }

        _pendingViewportRestore = Dispatcher.BeginInvoke(() =>
        {
            _pendingViewportRestore = null;
            TextArea.TextView.EnsureVisualLines();
            RestoreViewport(_viewportAnchor);
        }, DispatcherPriority.Background);
    }

    private void CancelViewportRestore()
    {
        _pendingViewportRestore?.Abort();
        _pendingViewportRestore = null;
        _viewportAnchor = null;
    }
}
