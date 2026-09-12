using DuCom.Core.Parsing;
using DuCom.ViewModels;

namespace DuCom.Controls;

public sealed partial class BoundedLogEditor
{
    private const int MaximumDocumentCharacters = 1_400_000;
    private readonly List<ProjectedLine> _projected = [];

    private void SynchronizeDocument(List<LogLineViewModel> target, ViewportAnchor? viewportAnchor)
    {
        if (target.Count == 0)
        {
            ResetDocument();
            return;
        }

        int overlapStart = FindOverlapStart(target);
        int removeCount = overlapStart < 0 ? _projected.Count : overlapStart;
        int retainedCount = overlapStart < 0 ? 0 : Math.Min(_projected.Count - overlapStart, target.Count);
        int removeCharacters = removeCount == 0 ? 0 : _projected[removeCount - 1].EndOffset;
        int appendStart = retainedCount;
        bool follow = FollowEnd && !_followSuppressed && SelectionLength == 0;
        int oldSelectionStart = SelectionStart;
        int oldSelectionLength = SelectionLength;

        if (removeCharacters > 0)
        {
            Document.Remove(0, removeCharacters);
            _projected.RemoveRange(0, removeCount);
            ShiftProjectedOffsets(-removeCharacters);
            ShiftSpans(-removeCharacters);
        }

        int matchingPrefix = FindMatchingPrefix(target, retainedCount);
        retainedCount = matchingPrefix;
        if (retainedCount < _projected.Count)
        {
            int offset = retainedCount == 0 ? 0 : _projected[retainedCount - 1].EndOffset;
            Document.Remove(offset, Document.TextLength - offset);
            _projected.RemoveRange(retainedCount, _projected.Count - retainedCount);
            _spans.RemoveAll(span => span.Offset >= offset);
            appendStart = retainedCount;
        }

        AppendLines(target, appendStart);
        Document.UndoStack.ClearAll();

        _colorizer.SetSpans(_spans);
        RestoreSelection(oldSelectionStart, oldSelectionLength, removeCharacters);
        if (follow && SelectionLength == 0)
        {
            ScheduleFollowRender();
        }
        else
        {
            TextArea.TextView.InvalidateMeasure();
            ScheduleViewportRestore(viewportAnchor);
        }
        ApplyCurrentMatch();
    }

    private void RebuildDocument(List<LogLineViewModel> target, ViewportAnchor? viewportAnchor)
    {
        _projected.Clear();
        _spans.Clear();
        _appliedMatch = null;
        _searchSelectionStart = -1;
        _searchSelectionLength = 0;

        System.Text.StringBuilder text = new();
        int documentOffset = 0;
        foreach (LogLineViewModel line in target)
        {
            int lineStart = documentOffset;
            int runOffset = 0;
            foreach (StyleRun run in line.StyledRuns)
            {
                if (run.Text.Length > 0)
                {
                    _spans.Add(new ColorSpan(lineStart + runOffset, run.Text.Length, run));
                    runOffset += run.Text.Length;
                }
            }

            text.Append(line.Text).AppendLine();
            documentOffset += line.Text.Length + Environment.NewLine.Length;
            _projected.Add(new ProjectedLine(line, lineStart, documentOffset));
        }

        Document.Text = text.ToString();
        Document.UndoStack.ClearAll();
        _colorizer.SetSpans(_spans);
        TextArea.TextView.InvalidateMeasure();
        if (FollowEnd && !_followSuppressed)
        {
            ScheduleFollowRender();
        }
        else
        {
            ScheduleViewportRestore(viewportAnchor);
        }
        ApplyCurrentMatch();
    }

    private List<LogLineViewModel> BuildBoundedTarget()
    {
        IReadOnlyList<LogLineViewModel> lines = Lines switch
        {
            IReadOnlyList<LogLineViewModel> list => list,
            null => [],
            _ => Lines.ToArray(),
        };
        List<LogLineViewModel> target = [];
        int characters = 0;
        for (int index = lines.Count - 1; index >= 0; index--)
        {
            int length = lines[index].Text.Length + Environment.NewLine.Length;
            if (target.Count > 0 && characters + length > MaximumDocumentCharacters)
            {
                break;
            }

            target.Add(lines[index]);
            characters += length;
        }
        target.Reverse();
        return target;
    }

    private int FindOverlapStart(List<LogLineViewModel> target)
    {
        if (_projected.Count == 0)
        {
            return 0;
        }

        LogLineViewModel first = target[0];
        for (int index = 0; index < _projected.Count; index++)
        {
            if (_projected[index].LogicalId == first.LogicalId && _projected[index].SegmentIndex == first.SegmentIndex)
            {
                return index;
            }
        }
        return -1;
    }

    private int FindMatchingPrefix(List<LogLineViewModel> target, int retainedCount)
    {
        if (retainedCount == 0)
        {
            return 0;
        }

        // Session projection mutates only by removing a prefix, appending a tail, or replacing
        // the final partial line. Immutable row identity avoids scanning the complete document
        // for the common append path.
        if (ReferenceEquals(_projected[retainedCount - 1].Source, target[retainedCount - 1]))
        {
            return retainedCount;
        }
        if (retainedCount > 1 && ReferenceEquals(_projected[retainedCount - 2].Source, target[retainedCount - 2]))
        {
            return retainedCount - 1;
        }

        int matchingPrefix = 0;
        while (matchingPrefix < retainedCount && Matches(_projected[matchingPrefix], target[matchingPrefix]))
        {
            matchingPrefix++;
        }
        return matchingPrefix;
    }

    private void ScheduleFollowRender()
    {
        if (_pendingFollowRender is { Status: System.Windows.Threading.DispatcherOperationStatus.Pending })
        {
            return;
        }

        _pendingFollowRender = Dispatcher.BeginInvoke(() =>
        {
            _pendingFollowRender = null;
            TextArea.TextView.InvalidateMeasure();
            if (FollowEnd && !_followSuppressed && SelectionLength == 0)
            {
                ScrollToEnd();
            }
        }, System.Windows.Threading.DispatcherPriority.Render);
    }

    private void AppendLines(List<LogLineViewModel> lines, int startIndex)
    {
        if (startIndex >= lines.Count)
        {
            return;
        }

        System.Text.StringBuilder text = new();
        int documentOffset = Document.TextLength;
        for (int index = startIndex; index < lines.Count; index++)
        {
            LogLineViewModel line = lines[index];
            int lineStart = documentOffset;
            int runOffset = 0;
            foreach (StyleRun run in line.StyledRuns)
            {
                if (run.Text.Length > 0)
                {
                    _spans.Add(new ColorSpan(lineStart + runOffset, run.Text.Length, run));
                    runOffset += run.Text.Length;
                }
            }

            text.Append(line.Text).AppendLine();
            documentOffset += line.Text.Length + Environment.NewLine.Length;
            _projected.Add(new ProjectedLine(line, lineStart, documentOffset));
        }
        Document.Insert(Document.TextLength, text.ToString());
    }

    private void RestoreSelection(int oldStart, int oldLength, int removedCharacters)
    {
        if (oldLength == 0)
        {
            return;
        }

        int start = oldStart - removedCharacters;
        if (start < 0 || start + oldLength > Document.TextLength)
        {
            Select(0, 0);
            return;
        }
        Select(start, oldLength);
    }

    private void ResetDocument()
    {
        _projected.Clear();
        _spans.Clear();
        _appliedMatch = null;
        _searchSelectionStart = -1;
        _searchSelectionLength = 0;
        _colorizer.SetSpans([]);
        Document.Text = string.Empty;
        Document.UndoStack.ClearAll();
        _memoryWarningDismissed = false;
    }
}
