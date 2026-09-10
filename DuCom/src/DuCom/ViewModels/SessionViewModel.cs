using CommunityToolkit.Mvvm.ComponentModel;
using DuCom.Core.Parsing;
using DuCom.Core.Sending;
using DuCom.Core.Sessions;
using DuCom.Core.Storage;
using DuCom.Services;

namespace DuCom.ViewModels;

public partial class SessionViewModel : ObservableObject, IAsyncDisposable
{
    private readonly IWorkspaceSession _session;
    private readonly ScriptGroupHost _commandGroupHost;

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

        HighlightRuleProjectId = highlightRuleProjectId is null || HighlightRuleProjects.Any(project => project.Id == highlightRuleProjectId)
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
}
