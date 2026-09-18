using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using DuCom.Core.LogAnalysis;
using DuCom.Core.Sessions;
using DuCom.Core.Storage;
using DuCom.Services;
using DuCom.ViewModels;
using Microsoft.Win32;
using Wpf.Ui.Controls;

namespace DuCom;

[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "WPF window lifetime disposes the cancellation source when the window closes or changes mode.")]
public partial class LogAnalyzerWindow : FluentWindow
{
    private const int MaximumRecords = 100_000;
    private const int EvictionBatchSize = 2_000;
    private const int MaximumRealtimeHistoryRecords = 20_000;
    private const int HistoryUiBatchSize = 250;
    private const int StaticUiBatchSize = 500;
    private const int RealtimePullPageSize = 2_048;
    private const int RealtimeMaximumSegmentsPerPull = 20_000;
    private readonly SessionWorkspaceViewModel _workspace;
    private readonly Func<bool> _isMemoryThresholdReached;
    private readonly LogAnalyzerRuleService _ruleService;
    private readonly ObservableCollection<LogAnalyzerRecord> _records = [];
    private readonly ConcurrentDictionary<string, SourceSettings> _sourceSettings = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<SessionViewModel, RealtimeSourceState> _realtimeStates = [];
    private readonly ConcurrentDictionary<string, BesSourceAnnotation> _sourceAnnotations = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _treeRefreshTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly DispatcherTimer _realtimePullTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private CancellationTokenSource? _fileOperationCancellation;
    private readonly LogAnalyzerPreferencesService _preferencesService = new();
    private LogAnalyzerPreferences _preferences;
    private IReadOnlyList<LogAnalyzerRule> _rules;
    private LogAnalyzerParser _parser;
    private AnalyzerTreeNode? _selectedNode;
    private long _sequence;
    private long _droppedChunks;
    private bool _realtimePullActive;
    private bool _realtimeMode;
    private bool _closed;
    private string[] _staticFilePaths = [];
    private AnalyzerNavigationMode _navigationMode;
    private long _fileOperationId;
    private bool _followLatest;
    private LogMessageDetailsWindow? _messageDetailsWindow;

    public LogAnalyzerWindow(SessionWorkspaceViewModel workspace, string rulesPath, Func<bool>? isMemoryThresholdReached = null)
    {
        _workspace = workspace;
        _preferences = _preferencesService.Load();
        _isMemoryThresholdReached = isMemoryThresholdReached ?? (() => false);
        _ruleService = new LogAnalyzerRuleService(rulesPath);
        _rules = _ruleService.Load();
        _parser = new LogAnalyzerParser(_rules);
        InitializeComponent();
        RecordsView = CollectionViewSource.GetDefaultView(_records);
        RecordsView.Filter = FilterRecord;
        DataContext = this;
        ApplyPreferences();
        _treeRefreshTimer.Tick += TreeRefreshTimer_Tick;
        _realtimePullTimer.Tick += RealtimePullTimer_Tick;
        RebuildTree();
        Loaded += LogAnalyzerWindow_Loaded;
        Closed += (_, _) =>
        {
            _closed = true;
            _treeRefreshTimer.Stop();
            _treeRefreshTimer.Tick -= TreeRefreshTimer_Tick;
            _realtimePullTimer.Stop();
            _realtimePullTimer.Tick -= RealtimePullTimer_Tick;
            _fileOperationCancellation?.Cancel();
            _fileOperationCancellation?.Dispose();
            StopRealtime();
            _messageDetailsWindow?.Close();
            SavePreferences();
        };
    }

    public ObservableCollection<AnalyzerTreeNode> AnalysisNodes { get; } = [];
    public ObservableCollection<AnalyzerSourceRow> Sources { get; } = [];
    public ICollectionView RecordsView { get; }

    private async void OpenFiles_Click(object sender, RoutedEventArgs e)
    {
        if (_realtimeMode) SetMode(realtime: false);
        OpenFileDialog dialog = new() { Multiselect = true, Filter = "Log files|*.log;*.txt;*.trace|All files|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        CancelFileOperation();
        long operationId = Interlocked.Increment(ref _fileOperationId);
        _fileOperationCancellation = new CancellationTokenSource();
        CancellationToken cancellationToken = _fileOperationCancellation.Token;
        ClearRecords();
        SetFileOperationUi(active: true);
        bool acceptingProgress = true;
        try
        {
            Progress<LogAnalyzerFileProgress> progress = new(value =>
            {
                if (!acceptingProgress || operationId != Volatile.Read(ref _fileOperationId) || _closed) return;
                LoadProgressBar.Value = value.Percentage;
                StatusText.Text = string.Format(System.Globalization.CultureInfo.CurrentCulture, Resource("LogAnalyzer.LoadProgressFormat"),
                    value.Percentage, value.FileIndex, value.FileCount, value.ProcessedLines, value.FileName);
            });
            LogAnalyzerFileLoadResult loaded = await Task.Run(() => LoadFiles(dialog.FileNames, progress, cancellationToken), cancellationToken);
            acceptingProgress = false;
            if (operationId != Volatile.Read(ref _fileOperationId)) return;
            _staticFilePaths = dialog.FileNames;
            Sources.Clear();
            for (int index = 0; index < dialog.FileNames.Length; index++)
            {
                string name = Path.GetFileName(dialog.FileNames[index]);
                LogAnalyzerSourcePreference saved = SourcePreference(name);
                string role = string.IsNullOrWhiteSpace(saved.Role) ? loaded.SourceRoles.GetValueOrDefault(dialog.FileNames[index], string.Empty) : saved.Role;
                Sources.Add(new AnalyzerSourceRow(dialog.FileNames[index], name, saved.IsSelected, role, saved.OffsetMilliseconds));
                _sourceSettings[dialog.FileNames[index]] = new SourceSettings(role, TimeSpan.FromMilliseconds(saved.OffsetMilliseconds));
            }
            LogAnalyzerRecord[] ordered = loaded.Records.OrderBy(record => record.DisplayTime).ThenBy(record => record.Sequence).ToArray();
            for (int offset = 0; offset < ordered.Length; offset += StaticUiBatchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int end = Math.Min(offset + StaticUiBatchSize, ordered.Length);
                for (int index = offset; index < end; index++) AddRecord(ordered[index]);
                double percentage = ordered.Length == 0 ? 100 : end * 100d / ordered.Length;
                LoadProgressBar.Value = percentage;
                StatusText.Text = string.Format(System.Globalization.CultureInfo.CurrentCulture, Resource("LogAnalyzer.BuildingListFormat"), percentage);
                await Dispatcher.Yield(DispatcherPriority.Background);
            }
            RebuildTree();
            ScrollToLatestIfEnabled();
            NavigationModeBox.SelectedIndex = 0;
            NavigationModeBox.IsEnabled = false;
            StatusText.Text = loaded.TruncatedLineCount == 0
                ? string.Format(System.Globalization.CultureInfo.CurrentCulture, Resource("LogAnalyzer.LoadedFormat"), _records.Count, dialog.FileNames.Length)
                : string.Format(System.Globalization.CultureInfo.CurrentCulture, Resource("LogAnalyzer.TruncatedFormat"), loaded.TotalLineCount, _records.Count, loaded.TruncatedLineCount);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = Resource("LogAnalyzer.Cancelled");
        }
        catch (Exception exception)
        {
            _staticFilePaths = [];
            StatusText.Text = exception.Message;
            Program.DiagnosticLog?.Warning("Log analyzer failed to load files.", exception);
        }
        finally
        {
            acceptingProgress = false;
            if (operationId == Volatile.Read(ref _fileOperationId)) SetFileOperationUi(active: false);
        }
    }

    private LogAnalyzerFileLoadResult LoadFiles(IReadOnlyList<string> paths, IProgress<LogAnalyzerFileProgress> progress, CancellationToken cancellationToken) =>
        LogAnalyzerFileLoader.Load(
            paths.Select(path => new LogAnalyzerFileSource(path, string.Empty)).ToArray(),
            _parser,
            MaximumRecords,
            () => Interlocked.Increment(ref _sequence),
            progress,
            cancellationToken);

    private void StaticMode_Click(object sender, RoutedEventArgs e) => SetMode(realtime: false);
    private void RealtimeMode_Click(object sender, RoutedEventArgs e) => SetMode(realtime: true);

    private void SetMode(bool realtime)
    {
        StaticModeButton.IsChecked = !realtime;
        RealtimeModeButton.IsChecked = realtime;
        if (_realtimeMode == realtime) return;
        _realtimeMode = realtime;
        CancelFileOperation();
        if (realtime) _staticFilePaths = [];
        StopRealtime();
        ClearPending();
        ClearRecords();
        NavigationModeBox.IsEnabled = realtime;
        if (realtime)
            NavigationModeBox.SelectedIndex = string.Equals(_preferences.NavigationMode, "Time", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        if (realtime) StartRealtime();
    }

    private void StartRealtime()
    {
        Sources.Clear();
        _sourceSettings.Clear();
        _sourceAnnotations.Clear();
        _realtimeStates.Clear();
        SessionViewModel[] sessions = _workspace.Sessions.Concat(_workspace.RightSessions).Distinct().Where(session => session.IsOpen).ToArray();
        if (sessions.Length == 0)
        {
            StatusText.Text = Resource("LogAnalyzer.NoOpenSessions");
            return;
        }

        for (int index = 0; index < sessions.Length; index++)
        {
            SessionViewModel session = sessions[index];
            LogAnalyzerSourcePreference saved = SourcePreference(session.PortName);
            Sources.Add(new AnalyzerSourceRow(session.WorkspaceSession.RuntimeId, session.PortName, saved.IsSelected, saved.Role, saved.OffsetMilliseconds));
            _sourceSettings[session.WorkspaceSession.RuntimeId] = new SourceSettings(saved.Role, TimeSpan.FromMilliseconds(saved.OffsetMilliseconds));
            _sourceAnnotations[session.WorkspaceSession.RuntimeId] = new BesSourceAnnotation(null, null);
            _realtimeStates[session] = new RealtimeSourceState();
        }
        RebuildTree();
        _realtimePullTimer.Interval = sessions.Length > 1 ? TimeSpan.FromSeconds(10) : TimeSpan.FromSeconds(1);
        StatusText.Text = string.Format(System.Globalization.CultureInfo.CurrentCulture, Resource("LogAnalyzer.RealtimePullFormat"), sessions.Length, _realtimePullTimer.Interval.TotalSeconds);
        _realtimePullTimer.Start();
        _ = PullRealtimeAsync(initialLoad: true);
    }

    private void RealtimePullTimer_Tick(object? sender, EventArgs e) => _ = PullRealtimeAsync(initialLoad: false);

    private async Task PullRealtimeAsync(bool initialLoad)
    {
        if (_realtimePullActive || !_realtimeMode || _closed) return;
        _realtimePullActive = true;
        bool pullAgain = false;
        try
        {
            if (_isMemoryThresholdReached())
            {
                StatusText.Text = Resource("LogAnalyzer.MemoryPressureStop");
                DropText.Text = Resource("LogAnalyzer.MemoryPressureHint");
                return;
            }
            SynchronizeRealtimeSources();
            SessionViewModel[] sessions = _realtimeStates.Keys.Where(session => session.IsOpen && IsSourceSelected(session.WorkspaceSession.RuntimeId)).ToArray();
            if (sessions.Length == 0)
            {
                StatusText.Text = Resource("LogAnalyzer.NoSelectedSources");
                return;
            }
            bool backlogRemaining = false;
            LogAnalyzerRecord[] pulled = await Task.Run(() =>
            {
                List<LogAnalyzerRecord> combined = [];
                foreach (SessionViewModel session in sessions)
                {
                    if (!_realtimeStates.TryGetValue(session, out RealtimeSourceState? state)) continue;
                    LineCursor? cursor = state.Cursor;
                    Queue<StoredLine>? initialTail = initialLoad ? new Queue<StoredLine>(Math.Max(1, MaximumRealtimeHistoryRecords / sessions.Length)) : null;
                    int pulledSegments = 0;
                    while (pulledSegments < RealtimeMaximumSegmentsPerPull)
                    {
                        LineStoreSnapshot snapshot = session.GetDisplaySnapshot(cursor, RealtimePullPageSize);
                        if (snapshot.Lines.Count == 0) break;
                        if (cursor is { } previousCursor && snapshot.FirstLogicalId is { } firstId && firstId > previousCursor.LogicalId + 1)
                            Interlocked.Add(ref _droppedChunks, firstId - previousCursor.LogicalId - 1);
                        foreach (StoredLine line in snapshot.Lines)
                        {
                            cursor = new LineCursor(line.LogicalId, line.SegmentIndex);
                            pulledSegments++;
                            if (initialTail is not null)
                            {
                                initialTail.Enqueue(line);
                                if (initialTail.Count > Math.Max(1, MaximumRealtimeHistoryRecords / sessions.Length)) initialTail.Dequeue();
                            }
                            else
                            {
                                AddPulledRecord(combined, session, line);
                            }
                        }
                        if (snapshot.Lines.Count < RealtimePullPageSize) break;
                    }
                    state.Cursor = cursor;
                    if (pulledSegments >= RealtimeMaximumSegmentsPerPull) backlogRemaining = true;
                    if (initialTail is not null)
                        foreach (StoredLine line in initialTail) AddPulledRecord(combined, session, line);
                }
                return combined.OrderBy(record => record.DisplayTime).ThenBy(record => record.Sequence).ToArray();
            });

            for (int offset = 0; offset < pulled.Length; offset += HistoryUiBatchSize)
            {
                int end = Math.Min(offset + HistoryUiBatchSize, pulled.Length);
                for (int index = offset; index < end; index++) AddRecord(pulled[index]);
                await Dispatcher.Yield(DispatcherPriority.Background);
            }
            UpdateSourceRows();
            ScheduleTreeRefresh();
            ScrollToLatestIfEnabled();
            StatusText.Text = string.Format(System.Globalization.CultureInfo.CurrentCulture,
                Resource("LogAnalyzer.RealtimePullStatusFormat"), sessions.Length, pulled.Length, _realtimePullTimer.Interval.TotalSeconds);
            DropText.Text = _droppedChunks == 0 ? string.Empty : string.Format(System.Globalization.CultureInfo.CurrentCulture, Resource("LogAnalyzer.DroppedLinesFormat"), _droppedChunks);
            pullAgain = backlogRemaining;
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
            Program.DiagnosticLog?.Warning("Log analyzer failed to pull realtime history.", exception);
        }
        finally { _realtimePullActive = false; }
        if (pullAgain) _ = Dispatcher.InvokeAsync(() => _ = PullRealtimeAsync(initialLoad: false), DispatcherPriority.Background);
    }

    private void AddPulledRecord(List<LogAnalyzerRecord> records, SessionViewModel session, StoredLine line)
    {
        string sourceId = session.WorkspaceSession.RuntimeId;
        BesSourceAnnotation detected = BesSourceAnnotationDetector.Detect(line.Text);
        BesSourceAnnotation previous = _sourceAnnotations.GetValueOrDefault(sourceId, new BesSourceAnnotation(null, null));
        BesSourceAnnotation annotation = new(detected.Side ?? previous.Side, detected.TwsRole ?? previous.TwsRole);
        _sourceAnnotations[sourceId] = annotation;
        SourceSettings settings = _sourceSettings.GetValueOrDefault(sourceId, new SourceSettings(annotation.Display, TimeSpan.Zero));
        string role = string.IsNullOrWhiteSpace(settings.Role) ? annotation.Display : settings.Role;
        records.Add(_parser.Parse(Interlocked.Increment(ref _sequence), sourceId, session.PortName, role, line.TimestampUtc, settings.Offset, line.Text));
    }

    private void UpdateSourceRows()
    {
        foreach (AnalyzerSourceRow row in Sources)
        {
            string detected = _sourceAnnotations.GetValueOrDefault(row.Id, new BesSourceAnnotation(null, null)).Display;
            if (!string.IsNullOrWhiteSpace(detected)) row.Role = detected;
            _sourceSettings[row.Id] = new SourceSettings(row.Role, TimeSpan.FromMilliseconds(row.OffsetMilliseconds));
        }
    }

    private void StopRealtime()
    {
        _realtimePullTimer.Stop();
        _realtimeStates.Clear();
        _sourceAnnotations.Clear();
    }

    private void AddRecord(LogAnalyzerRecord record)
    {
        _records.Add(record);
        if (_records.Count > MaximumRecords)
        {
            int removeCount = Math.Min(EvictionBatchSize, _records.Count - MaximumRecords + EvictionBatchSize - 1);
            for (int index = 0; index < removeCount; index++) _records.RemoveAt(0);
        }
    }

    private void Clear_Click(object sender, RoutedEventArgs e) => ClearRecords();
    private void ClearRecords()
    {
        _records.Clear();
        _selectedNode = null;
        RebuildTree();
        RecordsView.Refresh();
    }

    private void ClearPending()
    {
        _droppedChunks = 0;
        DropText.Text = string.Empty;
    }

    private void ApplyDetailedSettings_Click(object sender, RoutedEventArgs e)
    {
        ApplySourcesAndRefresh();
        ApplyColumnVisibility(ReadColumnVisibilityFromCheckboxes());
        SavePreferences();
    }

    private void ApplySourcesAndRefresh()
    {
        Dictionary<string, AnalyzerSourceRow> rows = Sources.ToDictionary(row => row.Id, StringComparer.OrdinalIgnoreCase);
        foreach (AnalyzerSourceRow source in Sources)
            _sourceSettings[source.Id] = new SourceSettings(source.Role, TimeSpan.FromMilliseconds(source.OffsetMilliseconds));
        LogAnalyzerRecord[] adjusted = _records.Select(record => rows.TryGetValue(record.SourceId, out AnalyzerSourceRow? source)
            ? record with
            {
                SourceRole = source.Role,
                DisplayTime = record.BaseDisplayTime + TimeSpan.FromMilliseconds(source.OffsetMilliseconds),
            }
            : record).OrderBy(record => record.DisplayTime).ThenBy(record => record.Sequence).ToArray();
        _records.Clear();
        foreach (LogAnalyzerRecord record in adjusted) _records.Add(record);
        RebuildTree();
        RecordsView.Refresh();
    }

    private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => RecordsView.Refresh();

    private void NavigationModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || NavigationModeBox.SelectedItem is not ComboBoxItem item) return;
        _navigationMode = string.Equals(item.Tag as string, "Time", StringComparison.Ordinal) ? AnalyzerNavigationMode.Time : AnalyzerNavigationMode.Keywords;
        _selectedNode = null;
        RebuildTree();
        RecordsView.Refresh();
        SavePreferences();
    }

    private void RulesMenu_Click(object sender, RoutedEventArgs e)
    {
        RulesContextMenu.PlacementTarget = RulesMenuButton;
        RulesContextMenu.IsOpen = true;
    }

    private void LogGrid_LoadingRow(object sender, System.Windows.Controls.DataGridRowEventArgs e)
    {
        e.Row.ClearValue(ForegroundProperty);
        e.Row.ClearValue(BackgroundProperty);
        if (e.Row.Item is not LogAnalyzerRecord { MatchedRules.Count: > 0 } record) return;
        LogAnalyzerRule rule = record.MatchedRules[0];
        if (rule.ForegroundR is { } foregroundR && rule.ForegroundG is { } foregroundG && rule.ForegroundB is { } foregroundB)
            e.Row.Foreground = new SolidColorBrush(Color.FromRgb(foregroundR, foregroundG, foregroundB));
        if (rule.BackgroundR is { } backgroundR && rule.BackgroundG is { } backgroundG && rule.BackgroundB is { } backgroundB)
            e.Row.Background = new SolidColorBrush(Color.FromRgb(backgroundR, backgroundG, backgroundB));
    }

    private void LogGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<DataGridCell>(e.OriginalSource as DependencyObject) is not { } cell || LogGrid.SelectedItem is not LogAnalyzerRecord record) return;
        (string title, string content) = DetailForColumn(cell.Column, record);
        if (_messageDetailsWindow is { IsLoaded: true } existing)
        {
            existing.ShowMessage(title, content);
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }

        LogMessageDetailsWindow window = new(title, content) { Owner = this };
        _messageDetailsWindow = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_messageDetailsWindow, window)) _messageDetailsWindow = null;
        };
        window.Show();
    }

    private (string Title, string Content) DetailForColumn(DataGridColumn column, LogAnalyzerRecord record)
    {
        if (ReferenceEquals(column, SourceLogColumn)) return (Resource("LogAnalyzer.Source"), record.SourceName);
        if (ReferenceEquals(column, TimeLogColumn)) return (Resource("LogAnalyzer.Time"), record.DisplayTime.ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.CurrentCulture));
        if (ReferenceEquals(column, MessageLogColumn)) return (Resource("LogAnalyzer.Message"), record.Message);
        if (ReferenceEquals(column, RoleLogColumn)) return (Resource("LogAnalyzer.Role"), record.SourceRole);
        if (ReferenceEquals(column, LevelLogColumn)) return (Resource("LogAnalyzer.Level"), record.Level);
        if (ReferenceEquals(column, ModuleLogColumn)) return (Resource("LogAnalyzer.Module"), record.Module);
        if (ReferenceEquals(column, KeywordsLogColumn)) return (Resource("LogAnalyzer.Keywords"), record.Keywords);
        if (ReferenceEquals(column, CommentLogColumn)) return (Resource("LogAnalyzer.Comment"), record.ChineseComment);
        return (Resource("LogAnalyzer.MessageDetails"), record.OriginalText);
    }

    private void AnalysisTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        _selectedNode = e.NewValue as AnalyzerTreeNode;
        RecordsView.Refresh();
    }

    private void AnalysisTree_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<System.Windows.Controls.TreeViewItem>(e.OriginalSource as DependencyObject) is { } item) item.IsSelected = true;
    }

    private void AnalysisTree_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (!CanUseSelectedStaticSource())
        {
            e.Handled = true;
            return;
        }
        ShowSourceInExplorerMenuItem.IsEnabled = CanRevealSelectedSource();
    }

    private void ShowSourceInExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (CanRevealSelectedSource()) RevealInExplorer(_selectedNode!.SourceId!);
    }

    private void CloseSourceFile_Click(object sender, RoutedEventArgs e)
    {
        if (!CanUseSelectedStaticSource()) return;
        string sourceId = _selectedNode!.SourceId!;
        string sourceName = _selectedNode.Name;
        _selectedNode = null;
        _staticFilePaths = _staticFilePaths.Where(path => !string.Equals(path, sourceId, StringComparison.OrdinalIgnoreCase)).ToArray();
        _sourceSettings.TryRemove(sourceId, out _);
        _sourceAnnotations.TryRemove(sourceId, out _);

        AnalyzerSourceRow? source = Sources.FirstOrDefault(row => string.Equals(row.Id, sourceId, StringComparison.OrdinalIgnoreCase));
        if (source is not null) Sources.Remove(source);
        for (int index = _records.Count - 1; index >= 0; index--)
        {
            if (string.Equals(_records[index].SourceId, sourceId, StringComparison.OrdinalIgnoreCase)) _records.RemoveAt(index);
        }

        RebuildTree();
        RecordsView.Refresh();
        StatusText.Text = _staticFilePaths.Length == 0
            ? Resource("LogAnalyzer.AllFilesClosed")
            : string.Format(System.Globalization.CultureInfo.CurrentCulture, Resource("LogAnalyzer.FileClosedFormat"), sourceName, _records.Count, _staticFilePaths.Length);
    }

    private bool CanUseSelectedStaticSource() => !_realtimeMode && _selectedNode is
    {
        Kind: AnalyzerNodeKind.Source,
        SourceId: { } sourceId,
    } && _staticFilePaths.Contains(sourceId, StringComparer.OrdinalIgnoreCase);

    private bool CanRevealSelectedSource() => CanUseSelectedStaticSource() && File.Exists(_selectedNode!.SourceId!);

    private bool FilterRecord(object item)
    {
        if (item is not LogAnalyzerRecord record) return false;
        if (Sources.FirstOrDefault(source => string.Equals(source.Id, record.SourceId, StringComparison.OrdinalIgnoreCase)) is { IsSelected: false }) return false;
        string search = SearchBox?.Text ?? string.Empty;
        if (!LogAnalyzerOperations.MatchesSearch(record, search)) return false;
        return _selectedNode?.Matches(record) ?? true;
    }

    private void RebuildTree()
    {
        IReadOnlyList<AnalyzerTreeNode> updated = _realtimeMode && _navigationMode == AnalyzerNavigationMode.Time
            ? BuildSourceTimeTree()
            : BuildSourceKeywordTree();
        AnalyzerTreeReconciler.Reconcile(AnalysisNodes, updated);
        _selectedNode = AnalyzerTreeReconciler.FindSelected(AnalysisNodes);
    }

    private IReadOnlyList<AnalyzerTreeNode> BuildSourceKeywordTree()
    {
        List<AnalyzerTreeNode> roots = [];
        foreach (IGrouping<string, LogAnalyzerRecord> sourceGroup in ActiveRecords().GroupBy(record => record.SourceId, StringComparer.OrdinalIgnoreCase).OrderBy(group => group.First().SourceName))
        {
            string sourceName = sourceGroup.First().SourceName;
            AnalyzerTreeNode source = new("source:" + sourceGroup.Key, SourceDisplayName(sourceGroup.Key, sourceName), AnalyzerNodeKind.Source,
                sourceGroup.Key, sourceGroup.Count(), sourceId: sourceGroup.Key);
            AddKeywordNodes(source, sourceGroup, sourceGroup.Key);
            roots.Add(source);
        }
        return roots;
    }

    private IReadOnlyList<AnalyzerTreeNode> BuildSourceTimeTree()
    {
        List<AnalyzerTreeNode> roots = [];
        foreach (IGrouping<string, LogAnalyzerRecord> sourceGroup in ActiveRecords().GroupBy(record => record.SourceId, StringComparer.OrdinalIgnoreCase).OrderBy(group => group.First().SourceName))
        {
            LogAnalyzerRecord[] records = sourceGroup.OrderBy(record => record.DisplayTime).ThenBy(record => record.Sequence).ToArray();
            string sourceName = records[0].SourceName;
            AnalyzerTreeNode source = new("source:" + sourceGroup.Key, SourceDisplayName(sourceGroup.Key, sourceName), AnalyzerNodeKind.Source,
                sourceGroup.Key, records.Length, sourceId: sourceGroup.Key);
            foreach (IGrouping<DateTimeOffset, LogAnalyzerRecord> minuteGroup in records.GroupBy(record => FloorMinute(record.DisplayTime)))
            {
                DateTimeOffset minute = minuteGroup.Key;
                AnalyzerTreeNode minuteNode = TimeNode($"{source.Key}:minute:{minute:O}", minute.ToString("HH:mm"), minuteGroup.Count(), minute, minute.AddMinutes(1), sourceGroup.Key);
                AddKeywordNodes(minuteNode, minuteGroup, sourceGroup.Key, minute, minute.AddMinutes(1));
                source.Children.Add(minuteNode);
            }
            roots.Add(source);
        }
        return roots;
    }

    private void AddKeywordNodes(AnalyzerTreeNode parent, IEnumerable<LogAnalyzerRecord> records, string sourceName,
        DateTimeOffset? start = null, DateTimeOffset? end = null)
    {
        LogAnalyzerRecord[] snapshot = records.ToArray();
        foreach (IGrouping<string, LogAnalyzerRule> category in _rules.GroupBy(rule => rule.Category).OrderBy(group => group.Key))
        {
            int categoryCount = snapshot.Count(record => record.MatchedRules.Any(rule => string.Equals(rule.Category, category.Key, StringComparison.OrdinalIgnoreCase)));
            if (categoryCount == 0) continue;
            AnalyzerTreeNode categoryNode = new($"{parent.Key}:category:{category.Key}", category.Key, AnalyzerNodeKind.Category,
                category.Key, categoryCount, start, end, sourceId: sourceName);
            foreach (LogAnalyzerRule rule in category)
            {
                int count = snapshot.Count(record => record.MatchedRules.Any(match => match.Id == rule.Id));
                if (count > 0) categoryNode.Children.Add(new AnalyzerTreeNode($"{categoryNode.Key}:rule:{rule.Id:D}", rule.Name,
                    AnalyzerNodeKind.Keyword, rule.Id.ToString("D"), count, start, end, sourceId: sourceName));
            }
            parent.Children.Add(categoryNode);
        }
        foreach (IGrouping<string, BluetoothAnalysisRecord> moduleGroup in snapshot
            .SelectMany(record => record.BluetoothAnalyses.Select(analysis => new BluetoothAnalysisRecord(record, analysis)))
            .GroupBy(item => item.Analysis.Module, StringComparer.OrdinalIgnoreCase).OrderBy(group => group.Key))
        {
            string categoryValue = "bluetooth:" + moduleGroup.Key;
            AnalyzerTreeNode bluetooth = new($"{parent.Key}:category:{categoryValue}", moduleGroup.Key, AnalyzerNodeKind.ProtocolCategory,
                categoryValue, moduleGroup.Select(item => item.Record.Sequence).Distinct().Count(), start, end, sourceId: sourceName);
            foreach (IGrouping<string, BluetoothAnalysisRecord> group in moduleGroup.GroupBy(item => item.Analysis.Keyword, StringComparer.OrdinalIgnoreCase).OrderBy(group => group.Key))
                bluetooth.Children.Add(new AnalyzerTreeNode($"{bluetooth.Key}:hci:{group.Key}", group.Key, AnalyzerNodeKind.ProtocolKeyword,
                    group.Key, group.Select(item => item.Record.Sequence).Distinct().Count(), start, end, sourceId: sourceName));
            parent.Children.Add(bluetooth);
        }
    }

    private static AnalyzerTreeNode TimeNode(string key, string name, int count, DateTimeOffset start, DateTimeOffset end, string sourceName) =>
        new(key, name, AnalyzerNodeKind.Time, string.Empty, count, start, end, sourceId: sourceName);

    private static DateTimeOffset FloorMinute(DateTimeOffset value) => value.AddTicks(-(value.Ticks % TimeSpan.TicksPerMinute));
    private IEnumerable<LogAnalyzerRecord> ActiveRecords() => _records.Where(record => IsSourceSelected(record.SourceId));
    private string SourceDisplayName(string sourceId, string sourceName)
    {
        AnalyzerSourceRow? row = Sources.FirstOrDefault(source => string.Equals(source.Id, sourceId, StringComparison.OrdinalIgnoreCase));
        return row is null || string.IsNullOrWhiteSpace(row.Role) ? sourceName : $"{sourceName}（{row.Role}）";
    }

    private async void ExportAnalysis_Click(object sender, RoutedEventArgs e)
    {
        if (_realtimeMode || _staticFilePaths.Length == 0)
        {
            StatusText.Text = Resource("LogAnalyzer.ExportStaticOnly");
            return;
        }
        CancelFileOperation();
        _fileOperationCancellation = new CancellationTokenSource();
        CancellationToken cancellationToken = _fileOperationCancellation.Token;
        SetFileOperationUi(active: true);
        long annotated = 0;
        try
        {
            Progress<(int Index, int Count, string SourcePath)> progress = new(value =>
            {
                StatusText.Text = string.Format(System.Globalization.CultureInfo.CurrentCulture, Resource("LogAnalyzer.ExportingFormat"),
                    value.Index, value.Count, Path.GetFileName(value.SourcePath));
                LoadProgressBar.Value = (value.Index - 1) * 100d / value.Count;
            });
            IReadOnlyList<LogAnalyzerExportResult> results = await LogAnalyzerExport.ExportAnnotatedCopiesAsync(
                _staticFilePaths, _parser, progress, cancellationToken);
            annotated = results.Sum(result => result.AnnotatedLines);
            LoadProgressBar.Value = 100;
            StatusText.Text = string.Format(System.Globalization.CultureInfo.CurrentCulture, Resource("LogAnalyzer.ExportCompletedFormat"), _staticFilePaths.Length, annotated);
            ThemedMessageDialogChoice choice = ThemedMessageDialog.ShowChoice(this,
                string.Format(System.Globalization.CultureInfo.CurrentCulture, Resource("LogAnalyzer.ExportCompletedPromptFormat"), results.Count, annotated),
                Resource("LogAnalyzer.ExportCompletedTitle"), ThemedMessageDialogKind.Information,
                "LogAnalyzer.ViewExportedFiles", "Dialog.Cancel");
            if (choice == ThemedMessageDialogChoice.Primary)
            {
                foreach (string outputPath in results.Select(result => result.OutputPath).Distinct(StringComparer.OrdinalIgnoreCase))
                    RevealInExplorer(outputPath);
            }
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = Resource("LogAnalyzer.Cancelled");
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
            Program.DiagnosticLog?.Warning("Log analyzer failed to export annotated files.", exception);
        }
        finally
        {
            SetFileOperationUi(active: false);
        }
    }

    private void CancelLoad_Click(object sender, RoutedEventArgs e) => _fileOperationCancellation?.Cancel();

    private void CancelFileOperation()
    {
        Interlocked.Increment(ref _fileOperationId);
        _fileOperationCancellation?.Cancel();
        _fileOperationCancellation?.Dispose();
        _fileOperationCancellation = null;
    }

    private void SetFileOperationUi(bool active)
    {
        LoadProgressBar.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        CancelLoadButton.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        ExportButton.IsEnabled = !active;
        OpenFilesButton.IsEnabled = !active;
        StaticModeButton.IsEnabled = !active;
        RealtimeModeButton.IsEnabled = !active;
        RulesMenuButton.IsEnabled = !active;
        if (!active) LoadProgressBar.Value = 0;
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match) return match;
            current = current is Visual or Visual3D ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current);
        }
        return null;
    }

    private void ScheduleTreeRefresh()
    {
        if (!_treeRefreshTimer.IsEnabled) _treeRefreshTimer.Start();
    }

    private void TreeRefreshTimer_Tick(object? sender, EventArgs e)
    {
        _treeRefreshTimer.Stop();
        RebuildTree();
    }

    private void ImportRules_Click(object sender, RoutedEventArgs e)
    {
        OpenFileDialog dialog = new() { Filter = "AnalyseDoc XML|*.xml" };
        if (dialog.ShowDialog(this) != true) return;
        ApplyRules(_ruleService.Import(dialog.FileName));
    }

    private void ResetRules_Click(object sender, RoutedEventArgs e) => ApplyRules(_ruleService.Reset());
    private void ApplyRules(IReadOnlyList<LogAnalyzerRule> rules)
    {
        _rules = rules;
        _parser = new LogAnalyzerParser(rules);
        RebuildTree();
        StatusText.Text = string.Format(System.Globalization.CultureInfo.CurrentCulture, Resource("LogAnalyzer.RulesFormat"), rules.Count);
    }

    private void EditRules_Click(object sender, RoutedEventArgs e)
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DuCom", "log-analyzer-rules.json");
        Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    private static string Resource(string key) => Application.Current.TryFindResource(key) as string ?? key;

    private static void RevealInExplorer(string path) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });

    private void FollowLatest_Click(object sender, RoutedEventArgs e)
    {
        _followLatest = FollowLatestButton.IsChecked == true;
        if (_followLatest) ScrollToLatestIfEnabled();
        SavePreferences();
    }

    private void ScrollToLatestIfEnabled()
    {
        if (!_followLatest || RecordsView.IsEmpty) return;
        Dispatcher.BeginInvoke(() =>
        {
            object? last = RecordsView.Cast<object>().LastOrDefault();
            if (last is not null) LogGrid.ScrollIntoView(last);
        }, DispatcherPriority.Render);
    }

    private void SynchronizeRealtimeSources()
    {
        SessionViewModel[] open = _workspace.Sessions.Concat(_workspace.RightSessions).Distinct().Where(session => session.IsOpen).ToArray();
        foreach (SessionViewModel session in open)
        {
            if (_realtimeStates.ContainsKey(session)) continue;
            _realtimeStates[session] = new RealtimeSourceState();
            LogAnalyzerSourcePreference saved = SourcePreference(session.PortName);
            Sources.Add(new AnalyzerSourceRow(session.WorkspaceSession.RuntimeId, session.PortName, saved.IsSelected, saved.Role, saved.OffsetMilliseconds));
            _sourceSettings[session.WorkspaceSession.RuntimeId] = new SourceSettings(saved.Role, TimeSpan.FromMilliseconds(saved.OffsetMilliseconds));
            _sourceAnnotations[session.WorkspaceSession.RuntimeId] = new BesSourceAnnotation(null, null);
        }
        foreach (SessionViewModel stale in _realtimeStates.Keys.Where(session => !open.Contains(session)).ToArray())
        {
            _realtimeStates.TryRemove(stale, out _);
            AnalyzerSourceRow? row = Sources.FirstOrDefault(source => string.Equals(source.Id, stale.WorkspaceSession.RuntimeId, StringComparison.OrdinalIgnoreCase));
            if (row is not null) Sources.Remove(row);
        }
    }

    private bool IsSourceSelected(string sourceId) => Sources.FirstOrDefault(source =>
        string.Equals(source.Id, sourceId, StringComparison.OrdinalIgnoreCase))?.IsSelected == true;

    private LogAnalyzerSourcePreference SourcePreference(string name) =>
        _preferences.Sources?.GetValueOrDefault(name, new LogAnalyzerSourcePreference()) ?? new LogAnalyzerSourcePreference();

    private void ApplyPreferences()
    {
        _followLatest = _preferences.FollowLatest;
        FollowLatestButton.IsChecked = _followLatest;
        DetailedSettingsExpander.IsExpanded = _preferences.SourceCalibrationExpanded;
        LogAnalyzerColumnVisibility columns = _preferences.Columns ?? new LogAnalyzerColumnVisibility();
        SetColumnCheckboxes(columns);
        ApplyColumnVisibility(columns);
        NavigationModeBox.SelectedIndex = string.Equals(_preferences.NavigationMode, "Time", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        _navigationMode = NavigationModeBox.SelectedIndex == 1 ? AnalyzerNavigationMode.Time : AnalyzerNavigationMode.Keywords;
        NavigationModeBox.IsEnabled = false;
        Width = Math.Max(MinWidth, _preferences.Width);
        Height = Math.Max(MinHeight, _preferences.Height);
        if (double.IsFinite(_preferences.Left) && double.IsFinite(_preferences.Top))
        {
            Left = _preferences.Left;
            Top = _preferences.Top;
            WindowStartupLocation = WindowStartupLocation.Manual;
        }
    }

    private void LogAnalyzerWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= LogAnalyzerWindow_Loaded;
        if (_preferences.RealtimeMode) SetMode(realtime: true);
    }

    private void SavePreferences()
    {
        if (!IsInitialized) return;
        Rect bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        Dictionary<string, LogAnalyzerSourcePreference> sources = _preferences.Sources is null
            ? new(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, LogAnalyzerSourcePreference>(_preferences.Sources, StringComparer.OrdinalIgnoreCase);
        foreach (AnalyzerSourceRow source in Sources)
            sources[source.Name] = new LogAnalyzerSourcePreference(source.IsSelected, source.Role, source.OffsetMilliseconds);
        string navigationMode = _realtimeMode ? _navigationMode.ToString() : _preferences.NavigationMode;
        _preferences = new LogAnalyzerPreferences(bounds.Left, bounds.Top, bounds.Width, bounds.Height, _realtimeMode,
            navigationMode, _followLatest, DetailedSettingsExpander.IsExpanded, sources, ReadAppliedColumnVisibility());
        _preferencesService.Save(_preferences);
    }

    private void SetColumnCheckboxes(LogAnalyzerColumnVisibility columns)
    {
        ShowSourceColumnCheckBox.IsChecked = columns.Source;
        ShowTimeColumnCheckBox.IsChecked = columns.Time;
        ShowMessageColumnCheckBox.IsChecked = columns.Message;
        ShowRoleColumnCheckBox.IsChecked = columns.Role;
        ShowLevelColumnCheckBox.IsChecked = columns.Level;
        ShowModuleColumnCheckBox.IsChecked = columns.Module;
        ShowKeywordsColumnCheckBox.IsChecked = columns.Keywords;
        ShowCommentColumnCheckBox.IsChecked = columns.Comment;
    }

    private LogAnalyzerColumnVisibility ReadColumnVisibilityFromCheckboxes() => new(
        ShowSourceColumnCheckBox.IsChecked == true,
        ShowTimeColumnCheckBox.IsChecked == true,
        ShowMessageColumnCheckBox.IsChecked == true,
        ShowRoleColumnCheckBox.IsChecked == true,
        ShowLevelColumnCheckBox.IsChecked == true,
        ShowModuleColumnCheckBox.IsChecked == true,
        ShowKeywordsColumnCheckBox.IsChecked == true,
        ShowCommentColumnCheckBox.IsChecked == true);

    private LogAnalyzerColumnVisibility ReadAppliedColumnVisibility() => new(
        SourceLogColumn.Visibility == Visibility.Visible,
        TimeLogColumn.Visibility == Visibility.Visible,
        MessageLogColumn.Visibility == Visibility.Visible,
        RoleLogColumn.Visibility == Visibility.Visible,
        LevelLogColumn.Visibility == Visibility.Visible,
        ModuleLogColumn.Visibility == Visibility.Visible,
        KeywordsLogColumn.Visibility == Visibility.Visible,
        CommentLogColumn.Visibility == Visibility.Visible);

    private void ApplyColumnVisibility(LogAnalyzerColumnVisibility columns)
    {
        SourceLogColumn.Visibility = ToVisibility(columns.Source);
        TimeLogColumn.Visibility = ToVisibility(columns.Time);
        MessageLogColumn.Visibility = ToVisibility(columns.Message);
        RoleLogColumn.Visibility = ToVisibility(columns.Role);
        LevelLogColumn.Visibility = ToVisibility(columns.Level);
        ModuleLogColumn.Visibility = ToVisibility(columns.Module);
        KeywordsLogColumn.Visibility = ToVisibility(columns.Keywords);
        CommentLogColumn.Visibility = ToVisibility(columns.Comment);
    }

    private static Visibility ToVisibility(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    private readonly record struct SourceSettings(string Role, TimeSpan Offset);
    private readonly record struct BluetoothAnalysisRecord(LogAnalyzerRecord Record, BluetoothHciAnalysis Analysis);
    private sealed class RealtimeSourceState { public LineCursor? Cursor { get; set; } }
}

public enum AnalyzerNodeKind { Root, Source, Level, Category, Keyword, ProtocolCategory, ProtocolKeyword, Time }
public enum AnalyzerNavigationMode { Keywords, Time }

public sealed class AnalyzerTreeNode(string key, string name, AnalyzerNodeKind kind, string value, int count,
    DateTimeOffset? start = null, DateTimeOffset? end = null, string? role = null, string? sourceId = null) : INotifyPropertyChanged
{
    public string Key { get; } = key;
    private string _name = name;
    private int _count = count;
    private bool _isExpanded;
    private bool _isSelected;

    public string Name
    {
        get => _name;
        set => SetField(ref _name, value);
    }
    public AnalyzerNodeKind Kind { get; } = kind;
    public string Value { get; } = value;
    public DateTimeOffset? Start { get; } = start;
    public DateTimeOffset? End { get; } = end;
    public string? Role { get; } = role;
    public string? SourceId { get; } = sourceId;
    public int Count
    {
        get => _count;
        set => SetField(ref _count, value);
    }
    public string DisplayName => $"{Name} ({Count})";
    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetField(ref _isExpanded, value);
    }
    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }
    public ObservableCollection<AnalyzerTreeNode> Children { get; } = [];

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool Matches(LogAnalyzerRecord record)
    {
        if (Start is { } start && record.DisplayTime < start || End is { } end && record.DisplayTime >= end) return false;
        if (!string.IsNullOrEmpty(SourceId) && !string.Equals(record.SourceId, SourceId, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.IsNullOrEmpty(Role) && !string.Equals(LogAnalyzerWindowRole(record), Role, StringComparison.OrdinalIgnoreCase)) return false;
        return Kind switch
        {
            AnalyzerNodeKind.Source => string.Equals(record.SourceId, Value, StringComparison.OrdinalIgnoreCase),
            AnalyzerNodeKind.Level => string.Equals(record.Level, Value, StringComparison.OrdinalIgnoreCase),
            AnalyzerNodeKind.Category => record.MatchedRules.Any(rule => string.Equals(rule.Category, Value, StringComparison.OrdinalIgnoreCase)),
            AnalyzerNodeKind.ProtocolCategory => record.BluetoothAnalyses.Any(analysis =>
                string.Equals("bluetooth:" + analysis.Module, Value, StringComparison.OrdinalIgnoreCase)),
            AnalyzerNodeKind.Keyword => record.MatchedRules.Any(rule => string.Equals(rule.Id.ToString("D"), Value, StringComparison.OrdinalIgnoreCase)),
            AnalyzerNodeKind.ProtocolKeyword => record.BluetoothAnalyses.Any(analysis =>
                string.Equals(analysis.Keyword, Value, StringComparison.OrdinalIgnoreCase)),
            _ => true,
        };
    }

    private static string LogAnalyzerWindowRole(LogAnalyzerRecord record) =>
        string.IsNullOrWhiteSpace(record.SourceRole) ? record.SourceName : record.SourceRole;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        if (propertyName is nameof(Name) or nameof(Count))
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayName)));
    }
}

internal static class AnalyzerTreeReconciler
{
    public static void Reconcile(ObservableCollection<AnalyzerTreeNode> current, IReadOnlyList<AnalyzerTreeNode> updated)
    {
        HashSet<string> updatedKeys = updated.Select(node => node.Key).ToHashSet(StringComparer.Ordinal);
        for (int index = current.Count - 1; index >= 0; index--)
        {
            if (!updatedKeys.Contains(current[index].Key)) current.RemoveAt(index);
        }

        for (int targetIndex = 0; targetIndex < updated.Count; targetIndex++)
        {
            AnalyzerTreeNode desired = updated[targetIndex];
            AnalyzerTreeNode? existing = current.FirstOrDefault(node => string.Equals(node.Key, desired.Key, StringComparison.Ordinal));
            if (existing is null)
            {
                current.Insert(targetIndex, desired);
                continue;
            }

            existing.Name = desired.Name;
            existing.Count = desired.Count;
            Reconcile(existing.Children, desired.Children);
            int currentIndex = current.IndexOf(existing);
            if (currentIndex != targetIndex) current.Move(currentIndex, targetIndex);
        }
    }

    public static AnalyzerTreeNode? FindSelected(IEnumerable<AnalyzerTreeNode> roots) =>
        Flatten(roots).FirstOrDefault(node => node.IsSelected);

    private static IEnumerable<AnalyzerTreeNode> Flatten(IEnumerable<AnalyzerTreeNode> roots)
    {
        foreach (AnalyzerTreeNode node in roots)
        {
            yield return node;
            foreach (AnalyzerTreeNode child in Flatten(node.Children)) yield return child;
        }
    }
}

public sealed class AnalyzerSourceRow(string id, string name, bool isSelected, string role, double offsetMilliseconds) : INotifyPropertyChanged
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    private bool _isSelected = isSelected;
    private string _role = role;
    private double _offsetMilliseconds = offsetMilliseconds;
    public bool IsSelected { get => _isSelected; set => SetField(ref _isSelected, value); }
    public string Role { get => _role; set => SetField(ref _role, value); }
    public double OffsetMilliseconds { get => _offsetMilliseconds; set => SetField(ref _offsetMilliseconds, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
