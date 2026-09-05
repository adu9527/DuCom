using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DuCom.Core.Parsing;
using DuCom.Core.Ports;
using DuCom.Core.Sending;
using DuCom.Core.Sessions;
using DuCom.Core.Storage;
using DuCom.Services;

namespace DuCom.ViewModels;

public partial class SessionViewModel : ObservableObject, IAsyncDisposable
{
    private readonly IWorkspaceSession _session;
    private readonly ScriptGroupHost _commandGroupHost;
    private readonly AnsiDisplayProjector _projector = new();
    private long? _renderedLastLogicalId;
    private int _renderedLastSegmentIndex = -1;
    private const int MaximumVisibleSegments = 30_000;
    private const int MaximumVisibleCharacters = 4 * 1024 * 1024;
    private const int MaximumSegmentsPerRender = 128;
    private const int MaximumWarnings = 50;
    private string _lastLoggedFault = string.Empty;
    private bool _regexTimeoutReported;
    private LineStoreSnapshot _visibleSearchSnapshot = new(null, null, 0, []);
    private bool _visibleSearchSnapshotDirty = true;
    private int _visibleCharacterCount;
    private const char EscapeCharacter = '\u001B';
    private readonly DispatcherTimer _timedSendTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _timedSendInProgress;

    internal SessionViewModel(
        IWorkspaceSession session,
        ReceiveDisplayMode receiveMode,
        bool timestampEnabled,
        bool loggingEnabled,
        SendMode initialSendMode = SendMode.Str,
        NewlinePolicy initialNewline = NewlinePolicy.None,
        bool followEnd = true,
        bool filterEnabled = true,
        IReadOnlyList<HighlightFilterRuleProject>? highlightRuleProjects = null,
        Guid? highlightRuleProjectId = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        PortName = session.Settings.PortName;
        _commandGroupHost = new ScriptGroupHost(
            canStart: () => IsOpen && SelectedCommandGroup is { Commands.Count: > 0 },
            send: (command, cancellationToken) =>
                _session.SendAsync(
                    command.IsHex ? SendMode.Hex : SendMode.Str,
                    command.Payload,
                    command.Newline,
                    cancellationToken).AsTask(),
            errorLogger: exception => Program.DiagnosticLog?.Error("Session command group run failed.", exception));
        _commandGroupHost.StateChanged += OnCommandGroupHostStateChanged;
        ReceiveMode = receiveMode;
        TimestampEnabled = timestampEnabled;
        LoggingEnabled = loggingEnabled;
        AppliedReceiveMode = receiveMode;
        AppliedTimestampEnabled = timestampEnabled;
        AppliedLoggingEnabled = loggingEnabled;
        SendMode = initialSendMode;
        Newline = initialNewline;
        FollowEnd = followEnd;
        FilterEnabled = filterEnabled;
        foreach (HighlightFilterRuleProject project in highlightRuleProjects ?? [])
        {
            HighlightRuleProjects.Add(project);
        }

        HighlightRuleProjectId = HighlightRuleProjects.Any(project => project.Id == highlightRuleProjectId)
            ? highlightRuleProjectId
            : HighlightRuleProjects.FirstOrDefault()?.Id;
        _session.Warning += OnSessionWarning;
        RefreshCommandGroups();
        RefreshState();
        Search.AttachSnapshotProvider(GetVisibleSearchSnapshot);
        _timedSendTimer.Tick += OnTimedSendTick;
    }

    public string PortName { get; }

    public int BaudRate => _session.Settings.BaudRate;

    internal void RefreshBaudRateDisplay() => OnPropertyChanged(nameof(BaudRate));

    internal ReceiveDisplayMode AppliedReceiveMode { get; }

    internal bool AppliedTimestampEnabled { get; }

    internal bool AppliedLoggingEnabled { get; }

    internal IWorkspaceSession WorkspaceSession => _session;

    /// <summary>Display tap fan-out for auxiliary surfaces (float send window, log filter).</summary>
    public SessionTapHub DisplayTaps => _session.DisplayTaps;

    public void RegisterDisplayTap(SessionDisplayTap tap) => _session.DisplayTaps.Register(tap);

    public bool UnregisterDisplayTap(string tapId) => _session.DisplayTaps.Unregister(tapId);

    public BatchObservableCollection<LogLineViewModel> VisibleLines { get; } = [];

    public ObservableCollection<string> Warnings { get; } = [];

    public SearchViewModel Search { get; } = new();

    public ObservableCollection<HighlightFilterRuleProject> HighlightRuleProjects { get; } = [];

    public IReadOnlyList<HighlightFilterRule> HighlightFilterRules =>
        HighlightRuleProjects.FirstOrDefault(project => project.Id == HighlightRuleProjectId)?.Rules ?? [];

    [ObservableProperty]
    public partial Guid? HighlightRuleProjectId { get; set; }

    partial void OnHighlightRuleProjectIdChanged(Guid? value)
    {
        ResetHighlightProjection();
    }

    public void ApplyHighlightRuleProject(Guid? projectId)
    {
        if (HighlightRuleProjectId == projectId)
        {
            ResetHighlightProjection();
            return;
        }

        HighlightRuleProjectId = projectId;
    }

    private void ResetHighlightProjection()
    {
        _projector.Reset();
        VisibleLines.Clear();
        _visibleCharacterCount = 0;
        _visibleSearchSnapshotDirty = true;
        _renderedLastLogicalId = null;
        _renderedLastSegmentIndex = -1;
    }

    public void ReplaceHighlightRuleProjects(IReadOnlyList<HighlightFilterRuleProject> projects)
    {
        Guid? previousProjectId = HighlightRuleProjectId;
        HighlightRuleProjects.Clear();
        foreach (HighlightFilterRuleProject project in projects)
        {
            HighlightRuleProjects.Add(project);
        }

        Guid? nextProjectId = previousProjectId is null
            ? null
            : HighlightRuleProjects.Any(project => project.Id == previousProjectId)
                ? previousProjectId
                : HighlightRuleProjects.FirstOrDefault(project => string.Equals(project.Name, "default", StringComparison.OrdinalIgnoreCase))?.Id
                    ?? HighlightRuleProjects.FirstOrDefault()?.Id;
        ApplyHighlightRuleProject(nextProjectId);
    }

    [ObservableProperty]
    public partial PortLifecycleState State { get; private set; }

    [ObservableProperty]
    public partial bool IsOpen { get; private set; }

    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    public partial bool HasFault { get; private set; }

    [ObservableProperty]
    public partial bool IsInRightPane { get; set; }

    [ObservableProperty]
    public partial bool AutoReconnect { get; set; }

    [ObservableProperty]
    public partial bool IsWaitingForReconnect { get; private set; }

    [ObservableProperty]
    public partial bool FollowEnd { get; set; } = true;

    [ObservableProperty]
    public partial ReceiveDisplayMode ReceiveMode { get; set; }

    [ObservableProperty]
    public partial bool TimestampEnabled { get; set; }

    [ObservableProperty]
    public partial bool LoggingEnabled { get; set; }

    [ObservableProperty]
    public partial bool FilterEnabled { get; set; } = true;

    [ObservableProperty]
    public partial string FaultMessage { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string SendText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool InterpretSendEscapes { get; set; }

    [ObservableProperty]
    public partial bool TimedSendEnabled { get; set; }

    [ObservableProperty]
    public partial int TimedSendIntervalMilliseconds { get; set; } = 1000;

    partial void OnTimedSendEnabledChanged(bool value) => UpdateTimedSendTimer();

    partial void OnTimedSendIntervalMillisecondsChanged(int value) => UpdateTimedSendTimer();

    [ObservableProperty]
    public partial SendMode SendMode { get; set; } = SendMode.Str;

    [ObservableProperty]
    public partial NewlinePolicy Newline { get; set; } = NewlinePolicy.None;

    [ObservableProperty]
    public partial long EvictedLineCount { get; private set; }

    /// <summary>Command groups available to this session (loaded from the command store).</summary>
    public ObservableCollection<CommandGroup> CommandGroups { get; } = [];

    [ObservableProperty]
    public partial CommandGroup? SelectedCommandGroup { get; set; }

    partial void OnSelectedCommandGroupChanged(CommandGroup? value) =>
        OnPropertyChanged(nameof(SelectedGroupCommands));

    /// <summary>Ordered commands of the selected group; re-computed on selection change.</summary>
    public IReadOnlyList<ScriptCommand> SelectedGroupCommands =>
        SelectedCommandGroup?.OrderedCommands() ?? [];

    [ObservableProperty]
    public partial bool IsCommandGroupRunning { get; private set; }

    private void OnCommandGroupHostStateChanged(object? sender, EventArgs e) =>
        IsCommandGroupRunning = _commandGroupHost.IsRunning;

    /// <summary>Reloads command groups from the store, keeping the current selection when possible.</summary>
    public void RefreshCommandGroups()
    {
        Guid? previousId = SelectedCommandGroup?.Id;
        CommandGroups.Clear();
        foreach (CommandGroup group in CommandScriptStore.Load())
        {
            CommandGroups.Add(group);
        }

        SelectedCommandGroup = previousId.HasValue
            ? CommandGroups.FirstOrDefault(group => group.Id == previousId.Value)
            : CommandGroups.FirstOrDefault(group => string.Equals(group.Name, DefaultDuComData.MyProjectName, StringComparison.Ordinal))
                ?? CommandGroups.FirstOrDefault();
    }

    /// <summary>Starts or stops looping the selected command group against this session.</summary>
    [RelayCommand]
    private async Task ToggleCommandGroupRunAsync()
    {
        if (IsCommandGroupRunning)
        {
            await _commandGroupHost.StopAsync();
            return;
        }

        if (SelectedCommandGroup is not null)
        {
            _commandGroupHost.Start(SelectedCommandGroup);
        }
    }

    /// <summary>Sends one scripted command immediately through this session.</summary>
    [RelayCommand]
    private async Task SendScriptCommandAsync(ScriptCommand? command)
    {
        if (command is null || !IsOpen)
        {
            return;
        }

        await _session.SendAsync(
            command.IsHex ? SendMode.Hex : SendMode.Str,
            command.Payload,
            command.Newline);
    }

    public bool HasEvictions => EvictedLineCount > 0;

    public string EvictionDisplay =>
        GetResourceString("Log.Evicted")
            .Replace("{0}", EvictedLineCount.ToString(CultureInfo.CurrentCulture), StringComparison.Ordinal);

    private static string GetResourceString(string key) =>
        Application.Current?.TryFindResource(key) as string ?? key;

    public async Task<PortCommandResult> OpenAsync(CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        try
        {
            PortCommandResult result = await _session.OpenAsync(cancellationToken);
            if (result == PortCommandResult.Succeeded)
            {
                FollowEnd = true;
            }
            return result;
        }
        finally
        {
            IsBusy = false;
            RefreshState();
            OnPropertyChanged(nameof(BaudRate));
        }
    }

    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        try
        {
            await _session.CloseAsync(cancellationToken);
        }
        finally
        {
            IsBusy = false;
            RefreshState();
            OnPropertyChanged(nameof(BaudRate));
        }
    }

    [RelayCommand]
    private async Task ToggleConnectionAsync()
    {
        IsWaitingForReconnect = false;
        if (IsBusy)
        {
            return;
        }

        if (IsOpen)
        {
            await CloseAsync();
        }
        else
        {
            await OpenAsync();
        }
    }

    internal void MarkDeviceRemoved() => IsWaitingForReconnect = AutoReconnect;

    internal void ClearReconnectWait() => IsWaitingForReconnect = false;

    public async Task ApplySettingsAsync(SerialPortSettings settings, CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        try
        {
            await _session.ApplySettingsAsync(settings, cancellationToken);
        }
        finally
        {
            IsBusy = false;
            RefreshState();
            OnPropertyChanged(nameof(BaudRate));
        }
    }

    public async Task SendAsync(CancellationToken cancellationToken = default)
    {
        if (!IsOpen || string.IsNullOrEmpty(SendText))
        {
            return;
        }

        string payload = SendMode == SendMode.Str && InterpretSendEscapes
            ? SendEscapeDecoder.Decode(SendText)
            : SendText;
        await _session.SendAsync(SendMode, payload, Newline, cancellationToken);
    }

    private void UpdateTimedSendTimer()
    {
        _timedSendTimer.Stop();
        if (TimedSendEnabled)
        {
            _timedSendTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(TimedSendIntervalMilliseconds, 50, 86_400_000));
            _timedSendTimer.Start();
        }
    }

    private async void OnTimedSendTick(object? sender, EventArgs e)
    {
        if (_timedSendInProgress || !IsOpen || string.IsNullOrEmpty(SendText))
        {
            return;
        }

        _timedSendInProgress = true;
        try
        {
            await SendAsync();
        }
        catch (Exception exception)
        {
            Program.DiagnosticLog?.Warning($"Timed send failed. Port={PortName}; {exception.Message}");
        }
        finally
        {
            _timedSendInProgress = false;
        }
    }

    /// <summary>Surfaces a watchdog hint in the session warning surface (UI thread).</summary>
    public void RaiseWatchdogHint(string hint) =>
        OnSessionWarning(this, new SessionWarningEventArgs(hint));

    /// <summary>
    /// Sends a raw command through the session (STR, no appended newline) — used by
    /// watchdog and Telnet bridge actions. Safe to call from background threads: the open
    /// check reads the thread-safe Core status snapshot and the payload mode is fixed
    /// instead of reading mutable ViewModel send options.
    /// </summary>
    public async Task SendRawCommandAsync(string command, CancellationToken cancellationToken = default)
    {
        if (_session.Status.State.State != PortLifecycleState.Open)
        {
            throw new InvalidOperationException("The session is not open.");
        }

        await _session.SendAsync(SendMode.Str, command, NewlinePolicy.None, cancellationToken);
    }

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
        EvictedLineCount = snapshot.EvictedLineCount;
        OnPropertyChanged(nameof(HasEvictions));
        OnPropertyChanged(nameof(EvictionDisplay));

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
            if (publishSearchSnapshot && _visibleSearchSnapshotDirty)
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

        if (FollowEnd)
        {
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
        }
        if (publishSearchSnapshot && _visibleSearchSnapshotDirty)
        {
            UpdateVisibleSearchSnapshot();
        }
        return stateChanged;
    }

    public async ValueTask DisposeAsync()
    {
        _timedSendTimer.Stop();
        _timedSendTimer.Tick -= OnTimedSendTick;
        _session.Warning -= OnSessionWarning;
        _commandGroupHost.StateChanged -= OnCommandGroupHostStateChanged;
        await _commandGroupHost.DisposeAsync();
        await _session.DisposeAsync();
        GC.SuppressFinalize(this);
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

    [RelayCommand]
    private void ClearWarnings()
    {
        Warnings.Clear();
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

    private void OnSessionWarning(object? sender, SessionWarningEventArgs e)
    {
        Warnings.Insert(0, e.Warning);
        while (Warnings.Count > MaximumWarnings)
        {
            Warnings.RemoveAt(Warnings.Count - 1);
        }
    }

    private void ReportRegexTimeout()
    {
        if (_regexTimeoutReported)
        {
            return;
        }

        _regexTimeoutReported = true;
        Program.DiagnosticLog?.Warning($"Highlight/filter regex timeout. Port={PortName}; Timeout={HighlightFilterRuleMatcher.MatchTimeout.TotalMilliseconds}ms");
        OnSessionWarning(this, new SessionWarningEventArgs(GetResourceString("Log.RegexTimeout")));
    }

    private bool RefreshState()
    {
        PortLifecycleState previousState = State;
        bool previousIsOpen = IsOpen;
        bool previousHasFault = HasFault;
        string previousFaultMessage = FaultMessage;
        DuCom.Core.Sessions.SerialSessionStatusSnapshot snapshot = _session.Status;
        PortLifecycleSnapshot state = snapshot.State;
        State = state.State;
        IsOpen = state.State == PortLifecycleState.Open;
        HasFault = snapshot.Fault is not null;
        string diagnosticFault = snapshot.Fault?.Message ?? string.Empty;
        FaultMessage = GetUserFaultMessage(diagnosticFault);
        if (HasFault && !string.Equals(_lastLoggedFault, FaultMessage, StringComparison.Ordinal))
        {
            _lastLoggedFault = FaultMessage;
            Program.DiagnosticLog?.Error($"Session fault. Port={PortName}; Source={snapshot.Fault?.Source}; Message={diagnosticFault}");
        }
        return previousState != State ||
            previousIsOpen != IsOpen ||
            previousHasFault != HasFault ||
            !string.Equals(previousFaultMessage, FaultMessage, StringComparison.Ordinal);
    }

    private static string GetUserFaultMessage(string message)
    {
        if (message.Contains("does not resolve to a valid serial port", StringComparison.OrdinalIgnoreCase))
        {
            return GetResourceString("Status.PortUnavailable");
        }

        if (message.Contains("Access to the path", StringComparison.OrdinalIgnoreCase) &&
            message.Contains("denied", StringComparison.OrdinalIgnoreCase))
        {
            return GetResourceString("Status.PortOccupied");
        }

        return message;
    }
}
