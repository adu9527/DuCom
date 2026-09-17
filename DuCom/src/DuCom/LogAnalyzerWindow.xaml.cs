using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using DuCom.Core.LogAnalysis;
using DuCom.Core.Sessions;
using DuCom.Core.Storage;
using DuCom.ViewModels;
using Microsoft.Win32;
using Wpf.Ui.Controls;

namespace DuCom;

[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "WPF window lifetime disposes the cancellation source when the window closes or changes mode.")]
public partial class LogAnalyzerWindow : FluentWindow
{
    private const int MaximumRecords = 100_000;
    private const int EvictionBatchSize = 2_000;
    private const int MaximumPendingChunks = 2_000;
    private const int MaximumRealtimeHistoryRecords = 20_000;
    private const int HistoryUiBatchSize = 250;
    private readonly SessionWorkspaceViewModel _workspace;
    private readonly LogAnalyzerRuleService _ruleService;
    private readonly ObservableCollection<LogAnalyzerRecord> _records = [];
    private readonly ConcurrentQueue<PendingChunk> _pending = new();
    private readonly ConcurrentDictionary<string, SourceSettings> _sourceSettings = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<SessionViewModel, string> _tapIds = [];
    private readonly Dictionary<string, string> _partialLines = [];
    private readonly DispatcherTimer _treeRefreshTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private CancellationTokenSource? _historyLoadCancellation;
    private IReadOnlyList<LogAnalyzerRule> _rules;
    private LogAnalyzerParser _parser;
    private AnalyzerTreeNode? _selectedNode;
    private long _sequence;
    private int _pendingCount;
    private long _droppedChunks;
    private int _flushScheduled;
    private volatile bool _historyLoading;
    private bool _realtimeMode;
    private bool _closed;

    public LogAnalyzerWindow(SessionWorkspaceViewModel workspace, string rulesPath)
    {
        _workspace = workspace;
        _ruleService = new LogAnalyzerRuleService(rulesPath);
        _rules = _ruleService.Load();
        _parser = new LogAnalyzerParser(_rules);
        InitializeComponent();
        RecordsView = CollectionViewSource.GetDefaultView(_records);
        RecordsView.Filter = FilterRecord;
        DataContext = this;
        _treeRefreshTimer.Tick += TreeRefreshTimer_Tick;
        RebuildTree();
        Closed += (_, _) =>
        {
            _closed = true;
            _treeRefreshTimer.Stop();
            _treeRefreshTimer.Tick -= TreeRefreshTimer_Tick;
            StopRealtime();
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
        ClearRecords();
        StatusText.Text = Resource("LogAnalyzer.Loading");
        try
        {
            LogAnalyzerFileLoadResult loaded = await Task.Run(() => LoadFiles(dialog.FileNames));
            Sources.Clear();
            for (int index = 0; index < dialog.FileNames.Length; index++)
            {
                Sources.Add(new AnalyzerSourceRow(dialog.FileNames[index], Path.GetFileName(dialog.FileNames[index]), RoleForIndex(index), 0));
                _sourceSettings[dialog.FileNames[index]] = new SourceSettings(RoleForIndex(index), TimeSpan.Zero);
            }
            foreach (LogAnalyzerRecord record in loaded.Records.OrderBy(record => record.DisplayTime).ThenBy(record => record.Sequence))
                AddRecord(record);
            RebuildTree();
            StatusText.Text = loaded.TruncatedLineCount == 0
                ? string.Format(System.Globalization.CultureInfo.CurrentCulture, Resource("LogAnalyzer.LoadedFormat"), _records.Count, dialog.FileNames.Length)
                : string.Format(System.Globalization.CultureInfo.CurrentCulture, Resource("LogAnalyzer.TruncatedFormat"), loaded.TotalLineCount, _records.Count, loaded.TruncatedLineCount);
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
            Program.DiagnosticLog?.Warning("Log analyzer failed to load files.", exception);
        }
    }

    private LogAnalyzerFileLoadResult LoadFiles(IReadOnlyList<string> paths) =>
        LogAnalyzerFileLoader.Load(
            paths.Select((path, index) => new LogAnalyzerFileSource(path, RoleForIndex(index))).ToArray(),
            _parser,
            MaximumRecords,
            () => Interlocked.Increment(ref _sequence));

    private void StaticMode_Click(object sender, RoutedEventArgs e) => SetMode(realtime: false);
    private void RealtimeMode_Click(object sender, RoutedEventArgs e) => SetMode(realtime: true);

    private void SetMode(bool realtime)
    {
        StaticModeButton.IsChecked = !realtime;
        RealtimeModeButton.IsChecked = realtime;
        if (_realtimeMode == realtime) return;
        _realtimeMode = realtime;
        StopRealtime();
        ClearPending();
        ClearRecords();
        if (realtime) StartRealtime();
    }

    private void StartRealtime()
    {
        Sources.Clear();
        _sourceSettings.Clear();
        SessionViewModel[] sessions = _workspace.Sessions.Concat(_workspace.RightSessions).Distinct().Where(session => session.IsOpen).ToArray();
        if (sessions.Length == 0)
        {
            StatusText.Text = Resource("LogAnalyzer.NoOpenSessions");
            return;
        }

        DateTimeOffset historyCutoffUtc = DateTimeOffset.UtcNow;
        _historyLoading = true;
        for (int index = 0; index < sessions.Length; index++)
        {
            SessionViewModel session = sessions[index];
            string role = ResolveRole(session, index);
            Sources.Add(new AnalyzerSourceRow(session.WorkspaceSession.RuntimeId, session.PortName, role, 0));
            _sourceSettings[session.WorkspaceSession.RuntimeId] = new SourceSettings(role, TimeSpan.Zero);
            string tapId = "log-analyzer-" + Guid.NewGuid().ToString("N");
            _tapIds[session] = tapId;
            session.RegisterDisplayTap(new SessionDisplayTap
            {
                Id = tapId,
                FormatSelector = () => SessionTapDisplayFormat.Str,
                Publish = _ => { },
                PublishTimestamped = (text, receivedAtUtc) =>
                {
                    if (receivedAtUtc >= historyCutoffUtc) Enqueue(session, role, receivedAtUtc, text);
                },
            });
        }
        RebuildTree();
        StatusText.Text = string.Format(System.Globalization.CultureInfo.CurrentCulture, Resource("LogAnalyzer.RealtimeHistoryLoadingFormat"), sessions.Length);
        _historyLoadCancellation = new CancellationTokenSource();
        _ = LoadRealtimeHistoryAsync(sessions, historyCutoffUtc, _historyLoadCancellation.Token);
    }

    private string ResolveRole(SessionViewModel session, int index)
    {
        if (_workspace.RightSessions.Contains(session)) return "R";
        if (ReferenceEquals(session, _workspace.SelectedSession)) return "L";
        return index switch { 0 => "L", 1 => "R", 2 => "C", _ => "S" + (index + 1) };
    }

    private static string RoleForIndex(int index) => index switch { 0 => "L", 1 => "R", 2 => "C", _ => "S" + (index + 1) };

    private async Task LoadRealtimeHistoryAsync(
        IReadOnlyList<SessionViewModel> sessions,
        DateTimeOffset cutoffUtc,
        CancellationToken cancellationToken)
    {
        try
        {
            LogAnalyzerParser parser = _parser;
            LogAnalyzerRecord[] history = await Task.Run(() =>
            {
                List<LogAnalyzerRecord> combined = new(MaximumRealtimeHistoryRecords);
                int perSessionLimit = Math.Max(1, MaximumRealtimeHistoryRecords / sessions.Count);
                foreach (SessionViewModel session in sessions)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Queue<LogAnalyzerRecord> recent = new(perSessionLimit);
                    string role = _sourceSettings.TryGetValue(session.WorkspaceSession.RuntimeId, out SourceSettings settings)
                        ? settings.Role
                        : string.Empty;
                    LineCursor? cursor = null;
                    while (true)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        LineStoreSnapshot snapshot = session.GetDisplaySnapshot(cursor, 2_048);
                        if (snapshot.Lines.Count == 0) break;
                        foreach (StoredLine line in snapshot.Lines)
                        {
                            cursor = new LineCursor(line.LogicalId, line.SegmentIndex);
                            if (line.TimestampUtc >= cutoffUtc) continue;
                            recent.Enqueue(parser.Parse(Interlocked.Increment(ref _sequence), session.WorkspaceSession.RuntimeId,
                                session.PortName, role, line.TimestampUtc, TimeSpan.Zero, line.Text));
                            if (recent.Count > perSessionLimit) recent.Dequeue();
                        }
                        if (snapshot.Lines.Count < 2_048) break;
                    }
                    combined.AddRange(recent);
                }
                return combined.OrderBy(record => record.DisplayTime).ThenBy(record => record.Sequence).ToArray();
            }, cancellationToken);

            for (int offset = 0; offset < history.Length; offset += HistoryUiBatchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int end = Math.Min(offset + HistoryUiBatchSize, history.Length);
                for (int index = offset; index < end; index++) AddRecord(history[index]);
                await Dispatcher.Yield(DispatcherPriority.Background);
            }

            ScheduleTreeRefresh();
            StatusText.Text = string.Format(System.Globalization.CultureInfo.CurrentCulture,
                Resource("LogAnalyzer.RealtimeHistoryLoadedFormat"), sessions.Count, history.Length);
            _historyLoading = false;
            SchedulePendingFlush();
        }
        catch (OperationCanceledException)
        {
            _historyLoading = false;
        }
        catch (Exception exception)
        {
            _historyLoading = false;
            StatusText.Text = exception.Message;
            Program.DiagnosticLog?.Warning("Log analyzer failed to load realtime history.", exception);
            SchedulePendingFlush();
        }
    }

    private void Enqueue(SessionViewModel session, string role, DateTimeOffset receivedAtUtc, string text)
    {
        if (_closed || string.IsNullOrEmpty(text)) return;
        if (Interlocked.Increment(ref _pendingCount) > MaximumPendingChunks)
        {
            Interlocked.Decrement(ref _pendingCount);
            Interlocked.Increment(ref _droppedChunks);
            return;
        }
        SourceSettings settings = _sourceSettings.TryGetValue(session.WorkspaceSession.RuntimeId, out SourceSettings current)
            ? current
            : new SourceSettings(role, TimeSpan.Zero);
        _pending.Enqueue(new PendingChunk(session.WorkspaceSession.RuntimeId, session.PortName, settings.Role, settings.Offset, receivedAtUtc, text));
        if (!_historyLoading) SchedulePendingFlush();
    }

    private void SchedulePendingFlush()
    {
        if (_closed || _pending.IsEmpty) return;
        if (Interlocked.Exchange(ref _flushScheduled, 1) == 0)
            Dispatcher.BeginInvoke(FlushPending, DispatcherPriority.Background);
    }

    private void FlushPending()
    {
        int processed = 0;
        while (processed < 200 && _pending.TryDequeue(out PendingChunk? chunk))
        {
            Interlocked.Decrement(ref _pendingCount);
            foreach (string line in SplitCompleteLines(chunk))
                AddRecord(_parser.Parse(Interlocked.Increment(ref _sequence), chunk.SourceId, chunk.SourceName, chunk.Role, chunk.ReceivedAt, chunk.Offset, line));
            processed++;
        }
        ScheduleTreeRefresh();
        DropText.Text = _droppedChunks == 0 ? string.Empty : string.Format(System.Globalization.CultureInfo.CurrentCulture, Resource("LogAnalyzer.DroppedFormat"), _droppedChunks);
        if (_pending.IsEmpty) Interlocked.Exchange(ref _flushScheduled, 0);
        else Dispatcher.BeginInvoke(FlushPending, DispatcherPriority.Background);
    }

    private IEnumerable<string> SplitCompleteLines(PendingChunk chunk)
    {
        string text = (_partialLines.TryGetValue(chunk.SourceId, out string? partial) ? partial : string.Empty) +
            chunk.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        string[] parts = text.Split('\n');
        _partialLines[chunk.SourceId] = parts[^1];
        return parts.Take(parts.Length - 1).Where(line => line.Length > 0);
    }

    private void StopRealtime()
    {
        _historyLoadCancellation?.Cancel();
        _historyLoadCancellation?.Dispose();
        _historyLoadCancellation = null;
        _historyLoading = false;
        foreach ((SessionViewModel session, string tapId) in _tapIds) session.UnregisterDisplayTap(tapId);
        _tapIds.Clear();
        _partialLines.Clear();
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
        while (_pending.TryDequeue(out _)) { }
        _partialLines.Clear();
        Interlocked.Exchange(ref _pendingCount, 0);
        Interlocked.Exchange(ref _flushScheduled, 0);
    }

    private void ApplySources_Click(object sender, RoutedEventArgs e)
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

    private void AnalysisTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        _selectedNode = e.NewValue as AnalyzerTreeNode;
        RecordsView.Refresh();
    }

    private bool FilterRecord(object item)
    {
        if (item is not LogAnalyzerRecord record) return false;
        string search = SearchBox?.Text ?? string.Empty;
        if (!LogAnalyzerOperations.MatchesSearch(record, search)) return false;
        return _selectedNode?.Kind switch
        {
            AnalyzerNodeKind.Source => string.Equals(record.SourceName, _selectedNode.Value, StringComparison.OrdinalIgnoreCase),
            AnalyzerNodeKind.Level => string.Equals(record.Level, _selectedNode.Value, StringComparison.OrdinalIgnoreCase),
            AnalyzerNodeKind.Category => record.MatchedRules.Any(rule => string.Equals(rule.Category, _selectedNode.Value, StringComparison.OrdinalIgnoreCase)),
            AnalyzerNodeKind.Keyword => record.MatchedRules.Any(rule => string.Equals(rule.Name, _selectedNode.Value, StringComparison.OrdinalIgnoreCase)),
            _ => true,
        };
    }

    private void RebuildTree()
    {
        LogAnalyzerAggregation aggregation = LogAnalyzerOperations.Aggregate(_records);
        List<AnalyzerTreeNode> updated =
        [
            GroupNode("sources", Resource("LogAnalyzer.Sources"), AnalyzerNodeKind.Source, aggregation.Sources),
            GroupNode("levels", Resource("LogAnalyzer.Levels"), AnalyzerNodeKind.Level, aggregation.Levels),
        ];
        AnalyzerTreeNode keywords = new("keywords", Resource("LogAnalyzer.KeywordGroups"), AnalyzerNodeKind.Root, string.Empty, _records.Count);
        foreach (IGrouping<string, LogAnalyzerRule> category in _rules.GroupBy(rule => rule.Category).OrderBy(group => group.Key))
        {
            AnalyzerTreeNode categoryNode = new($"category:{category.Key}", category.Key, AnalyzerNodeKind.Category, category.Key, aggregation.Categories.GetValueOrDefault(category.Key));
            foreach (LogAnalyzerRule rule in category)
            {
                int count = aggregation.Rules.GetValueOrDefault(rule.Id);
                if (count > 0) categoryNode.Children.Add(new AnalyzerTreeNode($"rule:{rule.Id:D}", rule.Name, AnalyzerNodeKind.Keyword, rule.Name, count));
            }
            keywords.Children.Add(categoryNode);
        }
        updated.Add(keywords);
        AnalyzerTreeReconciler.Reconcile(AnalysisNodes, updated);
        _selectedNode = AnalyzerTreeReconciler.FindSelected(AnalysisNodes);
    }

    private static AnalyzerTreeNode GroupNode(string key, string name, AnalyzerNodeKind kind, IReadOnlyDictionary<string, int> groups)
    {
        AnalyzerTreeNode root = new(key, name, AnalyzerNodeKind.Root, string.Empty, groups.Values.Sum());
        foreach ((string value, int count) in groups.OrderBy(group => group.Key))
            root.Children.Add(new AnalyzerTreeNode($"{key}:{value}", value, kind, value, count));
        return root;
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
    private sealed record PendingChunk(string SourceId, string SourceName, string Role, TimeSpan Offset, DateTimeOffset ReceivedAt, string Text);
    private readonly record struct SourceSettings(string Role, TimeSpan Offset);
}

public enum AnalyzerNodeKind { Root, Source, Level, Category, Keyword }

public sealed class AnalyzerTreeNode(string key, string name, AnalyzerNodeKind kind, string value, int count) : INotifyPropertyChanged
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

public sealed class AnalyzerSourceRow(string id, string name, string role, double offsetMilliseconds)
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public string Role { get; set; } = role;
    public double OffsetMilliseconds { get; set; } = offsetMilliseconds;
}
