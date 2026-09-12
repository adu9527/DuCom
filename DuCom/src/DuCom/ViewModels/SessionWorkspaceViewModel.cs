using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DuCom.Core.Parsing;
using DuCom.Core.Ports;
using DuCom.Core.Persistence;
using DuCom.Core.Sending;
using DuCom.Services;
using Microsoft.Win32;

namespace DuCom.ViewModels;

internal sealed record SessionWorkspaceCallbacks(
    Func<ApplicationSessionDefaults> GetDefaults,
    Func<WorkspaceSessionOptions, IWorkspaceSession> CreateWorkspaceSession,
    Func<IReadOnlyList<HighlightFilterRuleProject>> GetHighlightProjects,
    Func<string, bool> IsPortAvailable,
    Action<string?> SelectPort,
    Func<Task> WaitForPortRefresh,
    Func<Task> StopCommandRunner,
    Action MarkSettingsDirty,
    Action NotifySerialParameters,
    Action ResetSendHistory,
    Func<string, bool> RecordSendHistory,
    Action PersistSendHistory,
    Action<string> CloseToolWindows,
    Action<string> SetStatus,
    Func<string, string> Localize,
    Action ToggleDefaultReceiveMode,
    Action ToggleDefaultTimestamp,
    Func<SessionViewModel, Task<bool>> PrepareSessionClose,
    Action NotifyMainCommands);

public partial class SessionWorkspaceViewModel : ObservableObject, IAsyncDisposable
{
    private static readonly JsonSerializerOptions FormattedJsonOptions = new() { WriteIndented = true };
    private static readonly TimeSpan SlowOperationThreshold = TimeSpan.FromMilliseconds(50);
    private readonly SessionWorkspaceCallbacks _callbacks;
    private readonly PortOverrideStore _overrides = new();
    private List<string> _persistedRightPanePorts = [];
    private List<string> _persistedSessionOrder = [];
    private List<string> _persistedOpenSessionPorts = [];
    private string? _persistedSelectedSessionPort;
    private string? _persistedSelectedRightSessionPort;
    private SessionViewModel? _activeSession;
    private bool _sessionsRestored;
    private bool _loading;
    private bool _disposed;

    internal SessionWorkspaceViewModel(SessionWorkspaceCallbacks callbacks)
    {
        _callbacks = callbacks;
        Sessions.CollectionChanged += OnSessionsChanged;
        RightSessions.CollectionChanged += OnRightSessionsChanged;
    }

    public ObservableCollection<SessionViewModel> Sessions { get; } = [];
    public ObservableCollection<SessionViewModel> RightSessions { get; } = [];
    public bool HasSessions => Sessions.Count > 0;
    public bool IsSplitView => RightSessions.Count > 0;
    public SessionViewModel? ActiveSession => SessionWorkspacePolicy.ResolveActive(
        _activeSession, SelectedSession, SelectedRightSession, Sessions);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CloseCommand))]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    public partial SessionViewModel? SelectedSession { get; set; }

    [ObservableProperty]
    public partial SessionViewModel? SelectedRightSession { get; set; }

    partial void OnSelectedSessionChanged(SessionViewModel? value)
    {
        if (value is not null) _activeSession = value;
        OnPropertyChanged(nameof(ActiveSession));
        _callbacks.ResetSendHistory();
        _callbacks.NotifySerialParameters();
        NotifyCommandStates();
    }

    partial void OnSelectedRightSessionChanged(SessionViewModel? value)
    {
        if (value is not null) _activeSession = value;
        OnPropertyChanged(nameof(ActiveSession));
        _callbacks.NotifySerialParameters();
    }

    internal void SelectPort(string? portName)
    {
        if (string.IsNullOrWhiteSpace(portName)) return;
        SessionViewModel? session = FindSession(portName);
        if (session is null)
        {
            session = CreateClosedSession(portName);
            Sessions.Add(session);
        }
        if (!RightSessions.Contains(session)) SelectedSession = session;
    }

    internal void Activate(SessionViewModel session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (RightSessions.Contains(session)) SelectedRightSession = session;
        else if (Sessions.Contains(session)) SelectedSession = session;
        else return;
        _activeSession = session;
        _callbacks.NotifySerialParameters();
    }

    internal void ClearActiveDisplay() => ActiveSession?.ClearDisplay();
    internal void ToggleActiveFollowEnd()
    {
        if (ActiveSession is { } session) session.FollowEnd = !session.FollowEnd;
    }

    internal void ToggleActiveReceiveMode()
    {
        if (ActiveSession is { } session)
        {
            session.ReceiveMode = session.ReceiveMode == ReceiveDisplayMode.Str ? ReceiveDisplayMode.Hex : ReceiveDisplayMode.Str;
            RememberPortOverride(session.PortName);
        }
        else _callbacks.ToggleDefaultReceiveMode();
        _callbacks.SetStatus(_callbacks.Localize("Status.SessionSettingRequiresReopen"));
    }

    internal void ToggleActiveTimestamp()
    {
        if (ActiveSession is { } session)
        {
            session.TimestampEnabled = !session.TimestampEnabled;
            RememberPortOverride(session.PortName);
        }
        else _callbacks.ToggleDefaultTimestamp();
        _callbacks.SetStatus(_callbacks.Localize("Status.SessionSettingRequiresReopen"));
    }

    internal void ToggleActiveSendMode()
    {
        if (ActiveSession is not { } session) return;
        session.SendMode = session.SendMode == SendMode.Str ? SendMode.Hex : SendMode.Str;
        RememberPortOverride(session.PortName);
    }

    internal async Task<SessionViewModel?> OpenPortAsync(string portName, bool showFailureDialog)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        SessionViewModel? previous = SelectedSession;
        SessionViewModel? session = FindSession(portName);
        if (session is { IsOpen: false })
        {
            session = await RebuildClosedSessionAsync(session);
            if (session is null) return null;
        }
        bool created = false;
        if (session is null)
        {
            try
            {
                session = CreateClosedSession(portName);
                Sessions.Add(session);
                created = true;
                Program.DiagnosticLog?.Information($"Session created. Port={portName}; Baud={session.BaudRate}");
            }
            catch (Exception exception)
            {
                Program.DiagnosticLog?.Error($"Invalid port settings. Port={portName}; {exception.Message}");
                _callbacks.SetStatus(_callbacks.Localize("Status.InvalidPortSettings"));
                return null;
            }
        }

        bool rightPane = RightSessions.Contains(session);
        if (rightPane) SelectedRightSession = session;
        PortCommandResult result = await session.OpenAsync();
        if (!Sessions.Contains(session))
        {
            Program.DiagnosticLog?.Information($"Ignored late open result after session tab was closed. Port={portName}; Result={result}");
            NotifyCommandStates();
            return null;
        }

        if (result != PortCommandResult.Succeeded)
        {
            if (!rightPane && previous is not null && !ReferenceEquals(previous, session) && Sessions.Contains(previous)) SelectedSession = previous;
            else if (!rightPane) SelectedSession = session;
            string message = _callbacks.Localize("Status.OpenFailed").Replace("{0}", session.FaultMessage, StringComparison.Ordinal);
            _callbacks.SetStatus(message);
            Program.DiagnosticLog?.Warning($"Open failed. Port={portName}; Result={result}; Fault={session.FaultMessage}; CreatedNew={created}");
            if (showFailureDialog && ThemedMessageDialog.ShowOpenFailure(
                    Application.Current.MainWindow, message, _callbacks.Localize("Connection.OpenFailedTitle"), session))
            {
                await CloseSessionAsync(session);
            }
        }
        else
        {
            if (!rightPane) SelectedSession = session;
            _callbacks.SetStatus(string.Empty);
            if (session.IsOpen) RememberPortOverride(portName);
            Program.DiagnosticLog?.Information($"Open command completed. Port={portName}; Result={result}; IsOpen={session.IsOpen}; Fault={session.FaultMessage}");
        }

        NotifyCommandStates();
        stopwatch.Stop();
        if (stopwatch.Elapsed >= SlowOperationThreshold)
            Program.DiagnosticLog?.Warning($"Slow open command. Port={portName}; ElapsedMs={stopwatch.Elapsed.TotalMilliseconds:0.0}; IsOpen={session.IsOpen}");
        return session is { IsOpen: true } ? session : null;
    }

    internal async Task TogglePortAsync(string portName)
    {
        _callbacks.SelectPort(portName);
        SessionViewModel? session = FindSession(portName);
        if (session?.IsOpen == true)
        {
            if (RightSessions.Contains(session)) SelectedRightSession = session; else SelectedSession = session;
            string logPath = session.WorkspaceSession.CurrentLogFilePath ?? string.Empty;
            await session.CloseAsync();
            Program.DiagnosticLog?.Information($"Close command completed. Port={session.PortName}; IsOpen={session.IsOpen}; Fault={session.FaultMessage}; LogFile={logPath}");
            NotifyCommandStates();
        }
        else await OpenPortAsync(portName, true);
    }

    [RelayCommand(CanExecute = nameof(CanClose))]
    private async Task CloseAsync()
    {
        SessionViewModel session = SelectedSession!;
        string logPath = session.WorkspaceSession.CurrentLogFilePath ?? string.Empty;
        Stopwatch stopwatch = Stopwatch.StartNew();
        await session.CloseAsync();
        stopwatch.Stop();
        Program.DiagnosticLog?.Information($"Close command completed. Port={session.PortName}; ElapsedMs={stopwatch.Elapsed.TotalMilliseconds:0.0}; IsOpen={session.IsOpen}; Fault={session.FaultMessage}; LogFile={logPath}");
        NotifyCommandStates();
    }

    private bool CanClose() => SelectedSession is { IsOpen: true, IsBusy: false };

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        await SendCoreAsync(SelectedSession!, recordHistory: true);
        _callbacks.ResetSendHistory();
        NotifyCommandStates();
    }

    private bool CanSend() => SelectedSession is { IsOpen: true, IsBusy: false };

    [RelayCommand]
    private async Task SendSessionAsync(SessionViewModel session) => await SendCoreAsync(session, recordHistory: true);

    private async Task SendCoreAsync(SessionViewModel session, bool recordHistory)
    {
        try
        {
            await session.SendAsync();
            if (_callbacks.GetDefaults().FreezeAfterSend) session.FollowEnd = false;
            _callbacks.SetStatus(string.Empty);
            if (recordHistory && _callbacks.RecordSendHistory(session.SendText)) _callbacks.PersistSendHistory();
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException)
        {
            _callbacks.SetStatus(_callbacks.Localize("Status.InvalidHex").Replace("{0}", exception.Message, StringComparison.Ordinal));
            Program.DiagnosticLog?.Warning($"Send rejected. Port={session.PortName}; {exception.Message}");
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or TimeoutException or OperationCanceledException)
        {
            _callbacks.SetStatus(_callbacks.Localize("Status.SendFailed").Replace("{0}", exception.Message, StringComparison.Ordinal));
            Program.DiagnosticLog?.Warning($"Send failed. Port={session.PortName}.", exception);
        }
    }

    [RelayCommand]
    private async Task ToggleSessionConnectionAsync(SessionViewModel session)
    {
        try
        {
            if (session.IsOpen) await session.CloseAsync(); else await session.OpenAsync();
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or TimeoutException or OperationCanceledException)
        {
            _callbacks.SetStatus(_callbacks.Localize("Status.PortCommandFailed").Replace("{0}", exception.Message, StringComparison.Ordinal));
            Program.DiagnosticLog?.Warning($"Port command failed. Port={session.PortName}.", exception);
        }
        NotifyCommandStates();
    }

    [RelayCommand]
    private async Task CloseSessionAsync(SessionViewModel? session)
    {
        session ??= SelectedSession;
        if (session is null) return;
        if (!await TryCloseSessionAsync(session)) return;
        NotifyCommandStates();
    }

    private async Task<bool> TryCloseSessionAsync(SessionViewModel session)
    {
        if (!await _callbacks.PrepareSessionClose(session)) return false;
        bool selected = ReferenceEquals(SelectedSession, session);
        int index = Sessions.IndexOf(session);
        ThemedMessageDialog.CloseOpenFailureFor(session);
        if (session.IsOpen) await session.CloseAsync();
        RememberPortOverride(session.PortName);
        _callbacks.CloseToolWindows(session.PortName);
        if (RightSessions.Contains(session)) RemoveRightSession(session);
        Sessions.Remove(session);
        await session.DisposeAsync();
        if (selected || SelectedSession is null || !Sessions.Contains(SelectedSession))
        {
            List<SessionViewModel> left = [.. Sessions.Where(candidate => !RightSessions.Contains(candidate))];
            SelectedSession = left.Count == 0 ? null : left[Math.Clamp(index, 0, left.Count - 1)];
        }
        _callbacks.SelectPort(SelectedSession?.PortName);
        return true;
    }

    [RelayCommand]
    private async Task CloseRightPaneAsync(SessionViewModel? session)
    {
        session ??= SelectedRightSession;
        if (session is null) return;
        if (!await _callbacks.PrepareSessionClose(session)) return;
        RememberPortOverride(session.PortName);
        _callbacks.CloseToolWindows(session.PortName);
        RemoveRightSession(session);
        Sessions.Remove(session);
        await session.DisposeAsync();
        NotifyCommandStates();
    }

    internal async Task<bool> CloseAllSessionsForRestartAsync()
    {
        await _callbacks.StopCommandRunner();
        foreach (SessionViewModel session in Sessions.ToList())
        {
            if (!await TryCloseSessionAsync(session)) return false;
        }
        NotifyCommandStates();
        return true;
    }

    internal Task AssignRightPaneAsync(string portName)
    {
        SessionViewModel? primary = SelectedSession;
        SessionViewModel session = FindSession(portName) ?? CreateAndAdd(portName);
        SessionViewModel? left = primary is not null && !ReferenceEquals(primary, session) && !RightSessions.Contains(primary)
            ? primary
            : Sessions.FirstOrDefault(item => item.IsOpen && !ReferenceEquals(item, session) && !RightSessions.Contains(item))
              ?? Sessions.FirstOrDefault(item => !ReferenceEquals(item, session) && !RightSessions.Contains(item));
        if (left is null)
        {
            _callbacks.SetStatus(_callbacks.Localize("Status.SplitNeedsAnotherSession"));
            return Task.CompletedTask;
        }
        session.IsInRightPane = true;
        if (!RightSessions.Contains(session)) RightSessions.Add(session);
        SelectedRightSession = session;
        SelectedSession = left;
        _callbacks.SelectPort(left.PortName);
        _callbacks.SetStatus(string.Empty);
        return Task.CompletedTask;
    }

    [RelayCommand]
    private void MoveRightSessionToMain(SessionViewModel? session)
    {
        session ??= SelectedRightSession;
        if (session is null) return;
        RemoveRightSession(session);
        SelectedSession = session;
        _callbacks.SelectPort(session.PortName);
        NotifyCommandStates();
    }

    internal void MoveSessionTab(string portName, int targetIndex, bool rightPane)
    {
        ObservableCollection<SessionViewModel> collection = rightPane ? RightSessions : Sessions;
        SessionViewModel? session = collection.FirstOrDefault(item => string.Equals(item.PortName, portName, StringComparison.OrdinalIgnoreCase));
        if (session is null) return;
        int bounded = SessionWorkspacePolicy.BoundMoveIndex(targetIndex, collection.Count);
        int current = collection.IndexOf(session);
        if (current != bounded)
        {
            collection.Move(current, bounded);
            _callbacks.MarkSettingsDirty();
        }
    }

    internal void HandleDiscoveredPorts(IReadOnlyCollection<string> portNames)
    {
        HashSet<string> discovered = new(portNames, StringComparer.OrdinalIgnoreCase);
        foreach (SessionViewModel session in Sessions.Where(item => item.IsOpen && !discovered.Contains(item.PortName)).ToArray())
        {
            session.MarkDeviceRemoved();
            _ = CloseRemovedPortSessionAsync(session);
        }
        foreach (SessionViewModel session in Sessions.Where(item => item.IsWaitingForReconnect && discovered.Contains(item.PortName)).ToArray())
            _ = ReconnectReturnedPortAsync(session);
    }

    private async Task ReconnectReturnedPortAsync(SessionViewModel session)
    {
        if (!session.IsWaitingForReconnect || session.IsBusy || session.IsOpen) return;
        session.ClearReconnectWait();
        try
        {
            SessionViewModel? replacement = await RebuildClosedSessionAsync(session);
            if (replacement is null) return;
            replacement.AutoReconnect = true;
            PortCommandResult result = await replacement.OpenAsync();
            Program.DiagnosticLog?.Information($"Automatic reconnect completed. Port={replacement.PortName}; Result={result}");
        }
        catch (Exception exception) { Program.DiagnosticLog?.Warning($"Automatic reconnect failed. Port={session.PortName}.", exception); }
    }

    private static async Task CloseRemovedPortSessionAsync(SessionViewModel session)
    {
        try
        {
            Program.DiagnosticLog?.Warning($"Connected serial port disappeared from discovery; closing its session. Port={session.PortName}");
            await session.CloseAsync();
        }
        catch (Exception exception) { Program.DiagnosticLog?.Error($"Failed to close removed serial-port session. Port={session.PortName}", exception); }
    }

    internal void SetPersistedState(
        IReadOnlyDictionary<string, PortSettingSnapshot> overrides,
        IEnumerable<string> rightPorts,
        IEnumerable<string> sessionOrder,
        IEnumerable<string> openPorts,
        string? selectedPort,
        string? selectedRightPort)
    {
        _overrides.Replace(overrides);
        _persistedRightPanePorts = [.. rightPorts];
        _persistedSessionOrder = [.. sessionOrder];
        _persistedOpenSessionPorts = [.. openPorts];
        _persistedSelectedSessionPort = selectedPort;
        _persistedSelectedRightSessionPort = selectedRightPort;
    }

    internal async Task RestorePersistedSessionsAsync()
    {
        if (_sessionsRestored) return;
        _sessionsRestored = true;
        await _callbacks.WaitForPortRefresh();
        string[] openPorts = SessionWorkspacePolicy.ResolveOpenPorts(_persistedOpenSessionPorts, _persistedRightPanePorts, _persistedSessionOrder);
        if (openPorts.Length == 0) return;
        _loading = true;
        try
        {
            foreach (string port in openPorts)
            {
                if (!_callbacks.IsPortAvailable(port))
                {
                    Program.DiagnosticLog?.Warning($"Persisted session port is not currently available. Port={port}");
                    continue;
                }
                if (await OpenPortAsync(port, false) is null) Program.DiagnosticLog?.Warning($"Persisted session could not be reopened. Port={port}");
            }
            HashSet<string> rightPorts = new(_persistedRightPanePorts, StringComparer.OrdinalIgnoreCase);
            SessionViewModel? left = Sessions.FirstOrDefault(session => session.IsOpen && !rightPorts.Contains(session.PortName) &&
                string.Equals(session.PortName, _persistedSelectedSessionPort, StringComparison.OrdinalIgnoreCase))
                ?? Sessions.FirstOrDefault(session => session.IsOpen && !rightPorts.Contains(session.PortName));
            if (left is null) { left = Sessions.FirstOrDefault(session => session.IsOpen); rightPorts.Clear(); }
            foreach (SessionViewModel session in Sessions.Where(item => item.IsOpen && rightPorts.Contains(item.PortName) && !ReferenceEquals(item, left)))
            {
                session.IsInRightPane = true;
                if (!RightSessions.Contains(session)) RightSessions.Add(session);
            }
            SelectedRightSession = FindOpenSession(_persistedSelectedRightSessionPort, true) ?? RightSessions.FirstOrDefault(item => item.IsOpen);
            SelectedSession = left;
            _callbacks.SelectPort(left?.PortName);
        }
        finally { _loading = false; NotifyCommandStates(); }
    }

    internal Dictionary<string, PortSettingSnapshot> CapturePortOverrides() => _overrides.Capture(Sessions, _callbacks.GetDefaults());
    internal void RememberSessionHighlightProject(SessionViewModel session) => RememberPortOverride(session.PortName);
    internal void ApplySessionHighlightProject(SessionViewModel session, Guid? projectId)
    {
        session.ApplyHighlightRuleProject(projectId);
        RememberSessionHighlightProject(session);
    }

    internal void FormatJson()
    {
        if (SelectedSession is not { } session || string.IsNullOrWhiteSpace(session.SendText)) return;
        try
        {
            using JsonDocument document = JsonDocument.Parse(session.SendText);
            session.SendText = JsonSerializer.Serialize(document.RootElement, FormattedJsonOptions);
            _callbacks.SetStatus(string.Empty);
        }
        catch (JsonException exception) { _callbacks.SetStatus(_callbacks.Localize("Status.InvalidJson").Replace("{0}", exception.Message, StringComparison.Ordinal)); }
    }

    internal void JoinLines()
    {
        if (SelectedSession is { } session)
            session.SendText = session.SendText.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Replace('\n', ' ');
    }

    internal async Task LoadSendFileAsync(SessionViewModel? session)
    {
        session ??= ActiveSession;
        if (session is null) return;
        OpenFileDialog dialog = new() { Filter = _callbacks.Localize("Send.FileFilter"), CheckFileExists = true };
        if (dialog.ShowDialog() == true) session.SendText = await File.ReadAllTextAsync(dialog.FileName, Encoding.UTF8);
    }

    internal void OpenSearchFor(SessionViewModel session)
    {
        Activate(session);
        session.Search.OpenCommand.Execute(null);
    }

    private SessionViewModel CreateAndAdd(string portName)
    {
        SessionViewModel session = CreateClosedSession(portName);
        Sessions.Add(session);
        return session;
    }

    private SessionViewModel CreateClosedSession(string portName)
    {
        ApplicationSessionDefaults app = _callbacks.GetDefaults();
        SerialPortSettings settings = _overrides.ApplyTransport(portName, SerialPortSettings.Default(portName), app);
        _overrides.TryGet(portName, out PortSettingSnapshot? values);
        IReadOnlyList<HighlightFilterRuleProject> projects = _callbacks.GetHighlightProjects();
        PortSessionPreferences preferences = new(
            values?.ReceiveMode ?? app.ReceiveMode, values?.TimestampEnabled ?? app.TimestampEnabled,
            values?.LoggingEnabled ?? app.LoggingEnabled, app.LogDirectory,
            Math.Max(1, app.LogRotationMegabytes) * 1024L * 1024L, app.LogRotationEnabled,
            Math.Clamp(app.DisplayBudgetMegabytes, 16, 512) * 1024 * 1024, app.LogFileNameFormat,
            app.SendPrefixEnabled, app.SendPrefix, app.TimestampFormat, values?.FollowEnd ?? true,
            values?.FilterEnabled ?? true, values?.SendMode ?? app.SendMode, values?.Newline ?? app.Newline,
            values is { HighlightRuleChoiceMade: true } ? values.HighlightRuleProjectId
                : values?.HighlightRuleProjectId ?? (projects.Count > 0 ? projects[0].Id : null));
        SessionViewModel session = CreateSession(settings, preferences);
        session.AutoReconnect = values?.AutoReconnect ?? false;
        return session;
    }

    private SessionViewModel CreateSession(SerialPortSettings settings, PortSessionPreferences preferences)
    {
        WorkspaceSessionOptions options = new(settings, preferences.ReceiveMode, preferences.TimestampEnabled,
            preferences.LoggingEnabled, preferences.LogDirectory, preferences.LogRotationBytes,
            preferences.LogRotationEnabled, preferences.DisplayBudgetBytes, preferences.LogFileNameFormat,
            preferences.SendPrefixEnabled, preferences.SendPrefix, preferences.TimestampFormat);
        return new SessionViewModel(_callbacks.CreateWorkspaceSession(options), preferences.ReceiveMode,
            preferences.TimestampEnabled, preferences.LoggingEnabled, preferences.SendMode, preferences.Newline,
            preferences.FollowEnd, preferences.FilterEnabled, _callbacks.GetHighlightProjects(), preferences.HighlightRuleProjectId);
    }

    private async Task<SessionViewModel?> RebuildClosedSessionAsync(SessionViewModel session)
    {
        if (!await _callbacks.PrepareSessionClose(session)) return null;
        int index = Sessions.IndexOf(session);
        int rightIndex = RightSessions.IndexOf(session);
        bool selected = ReferenceEquals(SelectedSession, session);
        bool selectedRight = ReferenceEquals(SelectedRightSession, session);
        SerialPortSettings settings = session.WorkspaceSession.Settings;
        bool autoReconnect = session.AutoReconnect;
        _callbacks.CloseToolWindows(session.PortName);
        if (rightIndex >= 0) RightSessions.RemoveAt(rightIndex);
        Sessions.RemoveAt(index);
        await session.DisposeAsync();
        SessionViewModel replacement = CreateSession(settings, ResolvePreferences(settings.PortName));
        replacement.AutoReconnect = autoReconnect;
        Sessions.Insert(index, replacement);
        if (rightIndex >= 0) { replacement.IsInRightPane = true; RightSessions.Insert(Math.Min(rightIndex, RightSessions.Count), replacement); }
        if (selected) SelectedSession = replacement;
        if (selectedRight) SelectedRightSession = replacement;
        return replacement;
    }

    private PortSessionPreferences ResolvePreferences(string portName)
    {
        ApplicationSessionDefaults app = _callbacks.GetDefaults();
        _overrides.TryGet(portName, out PortSettingSnapshot? values);
        IReadOnlyList<HighlightFilterRuleProject> projects = _callbacks.GetHighlightProjects();
        return new PortSessionPreferences(values?.ReceiveMode ?? app.ReceiveMode, values?.TimestampEnabled ?? app.TimestampEnabled,
            values?.LoggingEnabled ?? app.LoggingEnabled, app.LogDirectory, Math.Max(1, app.LogRotationMegabytes) * 1024L * 1024L,
            app.LogRotationEnabled, Math.Clamp(app.DisplayBudgetMegabytes, 16, 512) * 1024 * 1024, app.LogFileNameFormat,
            app.SendPrefixEnabled, app.SendPrefix, app.TimestampFormat, values?.FollowEnd ?? true, values?.FilterEnabled ?? true,
            values?.SendMode ?? app.SendMode, values?.Newline ?? app.Newline,
            values is { HighlightRuleChoiceMade: true } ? values.HighlightRuleProjectId : values?.HighlightRuleProjectId ?? (projects.Count > 0 ? projects[0].Id : null));
    }

    internal void RememberPortOverride(string portName)
    {
        SessionViewModel? session = FindSession(portName);
        _overrides.Remember(portName, session?.WorkspaceSession.Settings, session, _callbacks.GetDefaults());
        if (!_loading) _callbacks.MarkSettingsDirty();
    }

    internal void RememberPortOverride(string portName, SerialPortSettings settings)
    {
        SessionViewModel? session = FindSession(portName);
        _overrides.Remember(portName, settings, session, _callbacks.GetDefaults());
        if (!_loading) _callbacks.MarkSettingsDirty();
    }

    private SessionViewModel? FindSession(string portName) => Sessions.FirstOrDefault(item => string.Equals(item.PortName, portName, StringComparison.OrdinalIgnoreCase));
    private SessionViewModel? FindOpenSession(string? portName, bool rightPane) => string.IsNullOrWhiteSpace(portName) ? null :
        Sessions.FirstOrDefault(session => session.IsOpen && session.IsInRightPane == rightPane && string.Equals(session.PortName, portName, StringComparison.OrdinalIgnoreCase));

    private void RemoveRightSession(SessionViewModel session)
    {
        session.IsInRightPane = false;
        RightSessions.Remove(session);
        if (SelectedSession is null || ReferenceEquals(SelectedSession, session))
            SelectedSession = Sessions.FirstOrDefault(item => item.IsOpen && !ReferenceEquals(item, session) && !RightSessions.Contains(item));
        _callbacks.SelectPort(SelectedSession?.PortName);
        NormalizeActiveSession(session);
    }

    private void OnSessionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        NormalizeActiveSession();
        OnPropertyChanged(nameof(HasSessions));
        if (!_loading) _callbacks.MarkSettingsDirty();
    }

    private void OnRightSessionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(IsSplitView));
        if (SelectedRightSession is null || !RightSessions.Contains(SelectedRightSession)) SelectedRightSession = RightSessions.LastOrDefault();
        if (!_loading) _callbacks.MarkSettingsDirty();
    }

    private void NotifyCommandStates()
    {
        CloseCommand.NotifyCanExecuteChanged();
        SendCommand.NotifyCanExecuteChanged();
        _callbacks.NotifyMainCommands();
    }

    internal void NotifyCommandStatesFromMain()
    {
        CloseCommand.NotifyCanExecuteChanged();
        SendCommand.NotifyCanExecuteChanged();
    }

    private bool IsCurrentSession(SessionViewModel? session) => session is not null && Sessions.Contains(session);

    private void NormalizeActiveSession(SessionViewModel? removed = null)
    {
        if (removed is null ? IsCurrentSession(_activeSession) : !ReferenceEquals(_activeSession, removed)) return;
        _activeSession = IsCurrentSession(SelectedSession) ? SelectedSession
            : IsCurrentSession(SelectedRightSession) ? SelectedRightSession
            : Sessions.FirstOrDefault();
        OnPropertyChanged(nameof(ActiveSession));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        Sessions.CollectionChanged -= OnSessionsChanged;
        RightSessions.CollectionChanged -= OnRightSessionsChanged;
        foreach (SessionViewModel session in Sessions.ToList()) await session.DisposeAsync();
        Sessions.Clear();
        RightSessions.Clear();
        GC.SuppressFinalize(this);
    }
}
