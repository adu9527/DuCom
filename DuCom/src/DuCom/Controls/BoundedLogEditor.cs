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
    private DispatcherOperation? _pendingFollowRender;
    private bool _followSuppressed;
    private bool _documentFrozen;
    private bool _forcePendingSync;
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

    /// <summary>Stops queued and future end-follow work before a binding update can arrive.</summary>
    public void PauseFollow()
    {
        _followSuppressed = true;
        _documentFrozen = true;
        _pendingSync?.Abort();
        _pendingSync = null;
        _forcePendingSync = false;
        _pendingFollowRender?.Abort();
        _pendingFollowRender = null;
        CancelViewportRestore();
    }

    public void ResumeFollow()
    {
        _followSuppressed = false;
        _documentFrozen = false;
        CancelViewportRestore();
        ScheduleSync(force: true);
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
        editor.ScheduleSync(force: true);
    }

    private static void OnFollowEndChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        BoundedLogEditor editor = (BoundedLogEditor)d;
        editor._followSuppressed = !(bool)e.NewValue;
        if ((bool)e.NewValue)
        {
            editor._documentFrozen = false;
            editor.CancelViewportRestore();
            // Apply everything accumulated while the user was inspecting a frozen view.
            editor.ScheduleSync(force: true);
        }
        else
        {
            editor._documentFrozen = true;
            editor._pendingSync?.Abort();
            editor._pendingSync = null;
            editor._forcePendingSync = false;
            editor._pendingFollowRender?.Abort();
            editor._pendingFollowRender = null;
            editor.CancelViewportRestore();
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
        ScheduleSync(force: true);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Unsubscribe();
        _pendingSync?.Abort();
        _pendingSync = null;
        _forcePendingSync = false;
        _pendingFollowRender?.Abort();
        _pendingFollowRender = null;
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

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_documentFrozen)
        {
            if (Lines is ICollection<LogLineViewModel> { Count: 0 })
            {
                ResetDocument();
            }
            return;
        }

        ScheduleSync();
    }

    private void ScheduleSync(bool force = false)
    {
        _forcePendingSync |= force;
        if (!IsLoaded || _documentFrozen && !_forcePendingSync ||
            _pendingSync is { Status: DispatcherOperationStatus.Pending })
        {
            return;
        }

        // Project before rendering. Running both the producer and AvalonEdit mutation at
        // Render priority can consume consecutive paint opportunities during receive bursts.
        _pendingSync = Dispatcher.BeginInvoke(SynchronizeDocumentSafe, DispatcherPriority.DataBind);
    }

    private void SynchronizeDocumentSafe()
    {
        bool force = _forcePendingSync;
        _forcePendingSync = false;
        long startedAt = Stopwatch.GetTimestamp();
        _pendingSync = null;
        if (_documentFrozen && !force)
        {
            return;
        }
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
