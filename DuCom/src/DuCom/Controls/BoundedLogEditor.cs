using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using DuCom.Core.Parsing;
using DuCom.Core.Search;
using DuCom.ViewModels;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace DuCom.Controls;

/// <summary>
/// Native selectable editor projection of the already bounded UI line collection. Collection
/// changes are coalesced and synchronized as prefix removal plus tail append, so sustained log
/// trimming never rebuilds the complete document.
/// </summary>
public sealed class BoundedLogEditor : TextEditor
{
    private const int MaximumDocumentCharacters = 4 * 1024 * 1024;
    private static readonly ConcurrentDictionary<int, SolidColorBrush> BrushCache = new();
    private readonly LogColorizer _colorizer = new();
    private readonly List<ProjectedLine> _projected = [];
    private readonly List<ColorSpan> _spans = [];
    private INotifyCollectionChanged? _observedCollection;
    private ScrollViewer? _observedScrollViewer;
    private DispatcherOperation? _pendingSync;
    private SearchMatch? _appliedMatch;
    private int _searchSelectionStart = -1;
    private int _searchSelectionLength;
    private bool _applyingSearchSelection;
    private bool _followSuppressed;
    private bool _memoryWarningDismissed;
    private long _nextMemoryCheckTimestamp;
    private long _nextSlowSyncLogTimestamp;
    private DispatcherOperation? _pendingViewportRestore;
    private ViewportAnchor? _viewportAnchor;

    public static readonly DependencyProperty LinesProperty = DependencyProperty.Register(
        nameof(Lines),
        typeof(IEnumerable<LogLineViewModel>),
        typeof(BoundedLogEditor),
        new PropertyMetadata(null, OnLinesChanged));

    public static readonly DependencyProperty FollowEndProperty = DependencyProperty.Register(
        nameof(FollowEnd),
        typeof(bool),
        typeof(BoundedLogEditor),
        new PropertyMetadata(true, OnFollowEndChanged));

    public static readonly DependencyProperty CurrentMatchProperty = DependencyProperty.Register(
        nameof(CurrentMatch),
        typeof(SearchMatch?),
        typeof(BoundedLogEditor),
        new PropertyMetadata(null, OnCurrentMatchChanged));

    public static readonly DependencyProperty ShowControlCharactersProperty = DependencyProperty.Register(
        nameof(ShowControlCharacters),
        typeof(bool),
        typeof(BoundedLogEditor),
        new PropertyMetadata(false, OnDisplayOptionsChanged));

    public static readonly DependencyProperty ShowSpacesProperty = DependencyProperty.Register(
        nameof(ShowSpaces),
        typeof(bool),
        typeof(BoundedLogEditor),
        new PropertyMetadata(false, OnDisplayOptionsChanged));

    public static readonly DependencyProperty ShowTabsProperty = DependencyProperty.Register(
        nameof(ShowTabs),
        typeof(bool),
        typeof(BoundedLogEditor),
        new PropertyMetadata(false, OnDisplayOptionsChanged));

    public BoundedLogEditor()
    {
        IsReadOnly = true;
        FontFamily = new FontFamily("Cascadia Mono, Consolas");
        HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto;
        VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto;
        Options.EnableHyperlinks = false;
        Options.EnableEmailHyperlinks = false;
        Options.HighlightCurrentLine = true;
        TextArea.SelectionBorder = null;
        TextArea.SelectionChanged += OnSelectionChanged;
        TextArea.SetBinding(ForegroundProperty, new Binding(nameof(Foreground)) { Source = this });
        TextArea.SetBinding(BackgroundProperty, new Binding(nameof(Background)) { Source = this });
        TextArea.TextView.LineTransformers.Add(_colorizer);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public IEnumerable<LogLineViewModel>? Lines
    {
        get => (IEnumerable<LogLineViewModel>?)GetValue(LinesProperty);
        set => SetValue(LinesProperty, value);
    }

    public bool FollowEnd
    {
        get => (bool)GetValue(FollowEndProperty);
        set => SetValue(FollowEndProperty, value);
    }

    /// <summary>Raised when the user scrolls a paused log back to the document end, so the host can restore <see cref="FollowEnd"/> and resume stacking.</summary>
    public event EventHandler? FollowEndResumedFromBottom;

    /// <summary>Stops queued and future end-follow work before a binding update can arrive.</summary>
    public void PauseFollow() => _followSuppressed = true;

    public void ResumeFollow()
    {
        _followSuppressed = false;
        ScheduleSync();
    }

    public SearchMatch? CurrentMatch
    {
        get => (SearchMatch?)GetValue(CurrentMatchProperty);
        set => SetValue(CurrentMatchProperty, value);
    }

    public bool ShowControlCharacters
    {
        get => (bool)GetValue(ShowControlCharactersProperty);
        set => SetValue(ShowControlCharactersProperty, value);
    }

    public bool ShowSpaces
    {
        get => (bool)GetValue(ShowSpacesProperty);
        set => SetValue(ShowSpacesProperty, value);
    }

    public bool ShowTabs
    {
        get => (bool)GetValue(ShowTabsProperty);
        set => SetValue(ShowTabsProperty, value);
    }

    private static void OnLinesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        BoundedLogEditor editor = (BoundedLogEditor)d;
        editor.Unsubscribe();
        editor.Subscribe();
        editor.ScheduleSync();
    }

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

    private static void OnFollowEndChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        BoundedLogEditor editor = (BoundedLogEditor)d;
        editor._followSuppressed = !(bool)e.NewValue;
        if ((bool)e.NewValue)
        {
            // Apply everything accumulated while the user was inspecting a frozen view.
            editor.ScheduleSync();
        }
    }

    private static void OnDisplayOptionsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        BoundedLogEditor editor = (BoundedLogEditor)d;
        editor.Options.ShowEndOfLine = editor.ShowControlCharacters;
        editor.Options.ShowSpaces = editor.ShowSpaces;
        editor.Options.ShowTabs = editor.ShowTabs;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Subscribe();
        Document.UndoStack.SizeLimit = 0;
        Document.UndoStack.ClearAll();
        _ = Dispatcher.BeginInvoke(SetVerticalScrollThumbMinimum, DispatcherPriority.Loaded);
        ScheduleSync();
    }

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

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Unsubscribe();
        if (_observedScrollViewer is not null)
        {
            _observedScrollViewer.ScrollChanged -= OnScrollViewerScrollChanged;
            _observedScrollViewer = null;
        }

        _pendingSync?.Abort();
        _pendingSync = null;
        _pendingViewportRestore?.Abort();
        _pendingViewportRestore = null;
    }

    private void Subscribe()
    {
        if (_observedCollection is not null || Lines is not INotifyCollectionChanged collection)
        {
            return;
        }

        _observedCollection = collection;
        collection.CollectionChanged += OnCollectionChanged;
    }

    private void Unsubscribe()
    {
        if (_observedCollection is null)
        {
            return;
        }

        _observedCollection.CollectionChanged -= OnCollectionChanged;
        _observedCollection = null;
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => ScheduleSync();

    private void ScheduleSync()
    {
        if (!IsLoaded || _pendingSync is { Status: DispatcherOperationStatus.Pending })
        {
            return;
        }

        // Run the coalesced document update with the render cadence. Background priority can
        // be starved by sustained CompositionTarget.Rendering callbacks and then jump in a
        // large burst, which is visible as a paused log followed by a batch refresh.
        _pendingSync = Dispatcher.BeginInvoke(SynchronizeDocumentSafe, DispatcherPriority.Render);
    }

    private void SynchronizeDocumentSafe()
    {
        long startedAt = Stopwatch.GetTimestamp();
        _pendingSync = null;
        ViewportAnchor? viewportAnchor = CaptureViewportAnchor();
        List<LogLineViewModel> target = BuildBoundedTarget();
        try
        {
            SynchronizeDocument(target, viewportAnchor);
        }
        catch (Exception exception)
        {
            Program.DiagnosticLog?.Error("AvalonEdit log synchronization failed; rebuilding the bounded document.", exception);
            try
            {
                RebuildDocument(target, viewportAnchor);
            }
            catch (Exception rebuildException)
            {
                Program.DiagnosticLog?.Error("AvalonEdit bounded document rebuild failed.", rebuildException);
            }
        }
        WarnForPausedMemoryGrowth();
        HookScrollViewer();
        SetVerticalScrollThumbMinimum();

        TimeSpan elapsed = Stopwatch.GetElapsedTime(startedAt);
        long now = Stopwatch.GetTimestamp();
        if (elapsed >= TimeSpan.FromMilliseconds(50) && now >= _nextSlowSyncLogTimestamp)
        {
            _nextSlowSyncLogTimestamp = now + Stopwatch.Frequency;
            string portName = DataContext is SessionViewModel session ? session.PortName : "unknown";
            Program.DiagnosticLog?.Warning(
                $"Slow log editor synchronization. Port={portName}; ElapsedMs={elapsed.TotalMilliseconds:0.0}; Lines={target.Count}; Characters={Document.TextLength}");
        }
    }

    private void HookScrollViewer()
    {
        if (_observedScrollViewer is not null)
        {
            return;
        }

        ScrollViewer? viewer = FindVisualDescendants<ScrollViewer>(this).FirstOrDefault();
        if (viewer is null)
        {
            return;
        }

        _observedScrollViewer = viewer;
        viewer.ScrollChanged += OnScrollViewerScrollChanged;
    }

    private void OnScrollViewerScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (FollowEnd && !_followSuppressed)
        {
            return;
        }

        double maximumOffset = Math.Max(0d, e.ExtentHeight - e.ViewportHeight);
        if (maximumOffset <= 0d || e.VerticalOffset >= maximumOffset - 1d)
        {
            // The user scrolled the paused log back to the bottom; a click that never
            // moved the viewport stays frozen (selection/inspection workflow).
            FollowEndResumedFromBottom?.Invoke(this, EventArgs.Empty);
        }
    }

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

        int matchingPrefix = 0;
        while (matchingPrefix < retainedCount && Matches(_projected[matchingPrefix], target[matchingPrefix]))
        {
            matchingPrefix++;
        }
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
        TextArea.TextView.InvalidateMeasure();
        RestoreSelection(oldSelectionStart, oldSelectionLength, removeCharacters);
        if (follow && SelectionLength == 0)
        {
            ScrollToEnd();
        }
        else
        {
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
            _projected.Add(new ProjectedLine(line.LogicalId, line.SegmentIndex, line.Text, line.StyledRuns, lineStart, documentOffset));
        }

        Document.Text = text.ToString();
        Document.UndoStack.ClearAll();
        _colorizer.SetSpans(_spans);
        TextArea.TextView.InvalidateMeasure();
        if (FollowEnd && !_followSuppressed)
        {
            ScrollToEnd();
        }
        else
        {
            ScheduleViewportRestore(viewportAnchor);
        }
        ApplyCurrentMatch();
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

    private List<LogLineViewModel> BuildBoundedTarget()
    {
        LogLineViewModel[] lines = Lines?.ToArray() ?? [];
        if (!FollowEnd || _followSuppressed)
        {
            return [.. lines];
        }

        List<LogLineViewModel> target = [];
        int characters = 0;
        for (int index = lines.Length - 1; index >= 0; index--)
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
            _projected.Add(new ProjectedLine(line.LogicalId, line.SegmentIndex, line.Text, line.StyledRuns, lineStart, documentOffset));
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

    private void WarnForPausedMemoryGrowth()
    {
        if (FollowEnd || !_followSuppressed || _memoryWarningDismissed || Document.TextLength == 0)
        {
            return;
        }

        long now = Stopwatch.GetTimestamp();
        if (now < _nextMemoryCheckTimestamp)
        {
            return;
        }
        _nextMemoryCheckTimestamp = now + 10L * Stopwatch.Frequency;

        long thresholdMiB = Window.GetWindow(this)?.DataContext is MainViewModel viewModel
            ? Math.Max(1, viewModel.PrivateMemoryThresholdMiB)
            : 1024;
        using Process process = Process.GetCurrentProcess();
        process.Refresh();
        long privateMemoryBytes = process.PrivateMemorySize64;
        if (privateMemoryBytes < thresholdMiB * 1024L * 1024L)
        {
            return;
        }

        _memoryWarningDismissed = true;
        string message = (Application.Current.TryFindResource("Log.PausedMemoryWarning") as string ??
            "The paused log view is retaining all display content and private memory has reached {0} MiB. Clear the display or resume scrolling to bound memory.")
            .Replace("{0}", (privateMemoryBytes / 1024d / 1024d).ToString("0", System.Globalization.CultureInfo.CurrentCulture), StringComparison.Ordinal);
        string title = Application.Current.TryFindResource("Log.PausedMemoryWarning.Title") as string ?? "Log memory warning";
        ThemedMessageDialog.Show(
            Window.GetWindow(this),
            message,
            title,
            ThemedMessageDialogKind.Warning);
    }

    private void ShiftProjectedOffsets(int delta)
    {
        for (int index = 0; index < _projected.Count; index++)
        {
            _projected[index] = _projected[index] with
            {
                StartOffset = _projected[index].StartOffset + delta,
                EndOffset = _projected[index].EndOffset + delta,
            };
        }
    }

    private void ShiftSpans(int delta)
    {
        _spans.RemoveAll(span => span.Offset + span.Length <= -delta);
        for (int index = 0; index < _spans.Count; index++)
        {
            _spans[index] = _spans[index] with { Offset = _spans[index].Offset + delta };
        }
    }

    private static bool Matches(ProjectedLine projected, LogLineViewModel line) =>
        projected.LogicalId == line.LogicalId &&
        projected.SegmentIndex == line.SegmentIndex &&
        string.Equals(projected.Text, line.Text, StringComparison.Ordinal) &&
        projected.StyledRuns.SequenceEqual(line.StyledRuns);

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

    private sealed record ProjectedLine(
        long LogicalId,
        int SegmentIndex,
        string Text,
        IReadOnlyList<StyleRun> StyledRuns,
        int StartOffset,
        int EndOffset);

    private sealed record ViewportAnchor(long LogicalId, int SegmentIndex, double OffsetWithinLine);

    private sealed record ColorSpan(int Offset, int Length, StyleRun Style);

    private sealed class LogColorizer : DocumentColorizingTransformer
    {
        private IReadOnlyList<ColorSpan> _spans = [];

        public void SetSpans(IReadOnlyList<ColorSpan> spans) => _spans = spans;

        protected override void ColorizeLine(DocumentLine line)
        {
            int lineEnd = line.EndOffset;
            int index = LowerBound(line.Offset);
            for (; index < _spans.Count; index++)
            {
                ColorSpan span = _spans[index];
                if (span.Offset >= lineEnd)
                {
                    break;
                }
                int start = Math.Max(line.Offset, span.Offset);
                int end = Math.Min(lineEnd, span.Offset + span.Length);
                if (start < end)
                {
                    ChangeLinePart(start, end, element => ApplyStyle(element, span.Style));
                }
            }
        }

        private int LowerBound(int offset)
        {
            int low = 0;
            int high = _spans.Count;
            while (low < high)
            {
                int middle = low + (high - low) / 2;
                if (_spans[middle].Offset + _spans[middle].Length <= offset)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle;
                }
            }
            return low;
        }

        private static void ApplyStyle(VisualLineElement element, StyleRun style)
        {
            // Resolve theme-aware defaults from the WPF resource dictionary so a run
            // without explicit colors still reads as "text on log surface" rather
            // than the hard-coded Brushes.Black/White (which would vanish on the
            // matching background in either theme).
            Brush defaultForeground = (Brush?)(Application.Current?.TryFindResource("Brush.LogText"))
                ?? Brushes.Gainsboro;
            Brush defaultBackground = (Brush?)(Application.Current?.TryFindResource("Brush.LogSurface"))
                ?? Brushes.Transparent;

            if (style.Inverse)
            {
                // ANSI inverse: foreground and background swap. When the run only
                // declares one of the two, fall back to the theme's surface/text
                // pairing so the swapped colours stay visible.
                Brush foreground = style.HasBackground
                    ? GetBrush(style.BackgroundR!.Value, style.BackgroundG!.Value, style.BackgroundB!.Value)
                    : defaultBackground;
                Brush background = style.HasForeground
                    ? GetBrush(style.ForegroundR!.Value, style.ForegroundG!.Value, style.ForegroundB!.Value)
                    : defaultForeground;
                element.TextRunProperties.SetForegroundBrush(foreground);
                element.BackgroundBrush = background;
            }
            else
            {
                if (style.HasForeground)
                {
                    element.TextRunProperties.SetForegroundBrush(GetBrush(style.ForegroundR!.Value, style.ForegroundG!.Value, style.ForegroundB!.Value));
                }
                else
                {
                    element.TextRunProperties.SetForegroundBrush(defaultForeground);
                }
                if (style.HasBackground)
                {
                    element.BackgroundBrush = GetBrush(style.BackgroundR!.Value, style.BackgroundG!.Value, style.BackgroundB!.Value);
                }
            }
            if (style.Bold)
            {
                element.TextRunProperties.SetTypeface(new Typeface(element.TextRunProperties.Typeface.FontFamily, element.TextRunProperties.Typeface.Style, FontWeights.Bold, element.TextRunProperties.Typeface.Stretch));
            }
            if (style.Italic)
            {
                element.TextRunProperties.SetTypeface(new Typeface(element.TextRunProperties.Typeface.FontFamily, FontStyles.Italic, element.TextRunProperties.Typeface.Weight, element.TextRunProperties.Typeface.Stretch));
            }
            if (style.Underline)
            {
                element.TextRunProperties.SetTextDecorations(System.Windows.TextDecorations.Underline);
            }
        }

        private static SolidColorBrush GetBrush(byte r, byte g, byte b)
        {
            int key = (r << 16) | (g << 8) | b;
            return BrushCache.GetOrAdd(key, static value =>
            {
                SolidColorBrush brush = new(Color.FromRgb((byte)(value >> 16), (byte)(value >> 8), (byte)value));
                brush.Freeze();
                return brush;
            });
        }
    }
}
