using System.Collections.Specialized;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using DuCom.Core.Parsing;
using DuCom.Core.Search;
using DuCom.ViewModels;
using ICSharpCode.AvalonEdit;

namespace DuCom.Controls;

/// <summary>
/// Native selectable editor projection of the already bounded UI line collection. Collection
/// changes are coalesced and synchronized as prefix removal plus tail append, so sustained log
/// trimming never rebuilds the complete document.
/// </summary>
public sealed partial class BoundedLogEditor : TextEditor
{
    private INotifyCollectionChanged? _observedCollection;
    private DispatcherOperation? _pendingSync;
    private bool _followSuppressed;
    private bool _memoryWarningDismissed;
    private long _nextMemoryCheckTimestamp;
    private long _nextSlowSyncLogTimestamp;

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

}
