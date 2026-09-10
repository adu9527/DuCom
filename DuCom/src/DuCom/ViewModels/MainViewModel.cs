using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using DuCom.Core.Diagnostics;
using DuCom.Core.Parsing;
using DuCom.Core.Ports;
using DuCom.Core.Persistence;
using DuCom.Core.Sending;
using DuCom.PluginHost;
using DuCom.PluginHost.Core;
using DuCom.Services;
using DuCom.Services.Plugins;
using DuCom.Services.Shortcuts;

namespace DuCom.ViewModels;

public partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private const string GitHubRepositoryUrl = "https://github.com/adu9527/DuCom";
    private const string ChineseUserManualUrl = "https://github.com/adu9527/DuCom/blob/main/Doc/UserManual.zh-CN.md";
    private const string EnglishUserManualUrl = "https://github.com/adu9527/DuCom/blob/main/Doc/UserManual.en-US.md";
    private static readonly JsonSerializerOptions ConfigurationJsonOptions = new() { WriteIndented = true };
    private readonly Func<WorkspaceSessionOptions, IWorkspaceSession> _sessionFactory;
    private readonly IPortDiscovery _portDiscovery;
    private static readonly TimeSpan MinimumRenderInterval = TimeSpan.FromSeconds(1d / 60d);
    private static readonly TimeSpan StatusRefreshInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan SlowOperationThreshold = TimeSpan.FromMilliseconds(50);
    private readonly HashSet<string> _hiddenPorts = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _settingsSaveTimer;
    private readonly DispatcherTimer _portSettingsApplyTimer;
    private string[] _discoveredPortNames = [];
    private IReadOnlyDictionary<string, DiscoveredPort> _discoveredPortDetails =
        new Dictionary<string, DiscoveredPort>(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;
    private bool _isLoadingSettings;
    private bool _settingsDirty;
    private bool _portSettingsApplyPending;
    private bool _allowSerialParametersWindowClose;
    private Dictionary<string, PortSettingSnapshot> _portOverrides = new(StringComparer.OrdinalIgnoreCase);
    private string[] _commandTargetPortNames = [];
    private List<string> _persistedRightPanePorts = [];
    private List<string> _persistedSessionOrder = [];
    private List<string> _persistedOpenSessionPorts = [];
    private string? _persistedSelectedSessionPort;
    private string? _persistedSelectedRightSessionPort;
    private bool _sessionsRestored;
    private SessionViewModel? _activeLogSession;
    private TimeSpan _lastRenderTime;
    private TimeSpan _lastStatusRefreshTime;
    private readonly object _portRefreshSync = new();
    private bool _portRefreshRequested;
    private Task? _portRefreshTask;
    private long _nextSlowProjectionLogTimestamp;

    internal MainViewModel(
        IPortDiscovery portDiscovery,
        Func<WorkspaceSessionOptions, IWorkspaceSession> sessionFactory)
    {
        _portDiscovery = portDiscovery ?? throw new ArgumentNullException(nameof(portDiscovery));
        _sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
        CompositionTarget.Rendering += OnCompositionRendering;
        _settingsSaveTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _settingsSaveTimer.Tick += OnSettingsSaveTick;
        _portSettingsApplyTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        _portSettingsApplyTimer.Tick += OnPortSettingsApplyTick;
        ShortcutManager = new ShortcutManager();
        ShortcutManager.RegisterDefaultActions();
        ShortcutsSettings = new ShortcutsSettingsViewModel(ShortcutManager);
        HighlightFilterSettings = new HighlightFilterRulesViewModel(
            new HighlightFilterRuleService(HighlightFilterRulesFilePath));
        HighlightFilterSettings.Saved += (_, _) => LoadHighlightFilterRules();
        HighlightFilterSettings.Applied += OnHighlightRulesApplied;
        HighlightFilterSettings.ProjectsChanged += OnHighlightRuleProjectsChanged;
        _sendHistoryNavigator = new SendHistoryNavigator(_sendHistory);
        Sessions.CollectionChanged += OnSessionsChanged;
        RightSessions.CollectionChanged += OnRightSessionsChanged;
        SessionProbes = new Services.SessionProbeProvider(Sessions);
        CommandRunner = new CommandGroupRunnerHost(
            () => Volatile.Read(ref _commandTargetPortNames),
            () => SessionProbes.CommandSnapshot,
            key => Application.Current.Dispatcher.BeginInvoke(() => StatusMessage = GetResourceString(key)));
        Telnet = new Services.TelnetBridgeService(SessionProbes, new DuCom.Core.Telnet.BasicTelnetServer());
        Watchdog = new Services.WatchdogService(SessionProbes);
        VariableMonitor = new Services.VariableMonitorService(SessionProbes);
        LoadWatchdogRules();
        LoadMonitorRules();
        LoadShortcuts();
        LoadSettings();
        Services.Updates.AppUpdateService.Instance.AttachSettings(
            () => SkippedUpdateVersion,
            value => SkippedUpdateVersion = value);
        PluginManager = new PluginManagerViewModel(this);
        LoadHighlightFilterRules();
        SendHistoryFileService.LoadInto(_sendHistory);
        SyncAppearanceSelection();
        _ = RefreshPortsAsync();
        ApplySystemBehaviorSettings();
    }

    internal CommandGroupRunnerHost CommandRunner { get; }

    internal IReadOnlyList<string> CommandTargetPortNames => Volatile.Read(ref _commandTargetPortNames);

    internal void SetCommandTargetPortNames(IEnumerable<string> portNames)
    {
        string[] normalized = [.. portNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(name => name, StringComparer.Ordinal)];
        Volatile.Write(ref _commandTargetPortNames, normalized);
        MarkSettingsDirty();
    }

    public ObservableCollection<WatchdogRule> WatchdogRules { get; } = [];

    internal Services.WatchdogService Watchdog { get; }

    internal Services.VariableMonitorService VariableMonitor { get; }

    internal Services.SessionProbeProvider SessionProbes { get; }

    internal Services.TelnetBridgeService Telnet { get; }

    public ShortcutManager ShortcutManager { get; }

    /// <summary>Shortcuts management hosted in the settings window; shares ShortcutManager.</summary>
    public ShortcutsSettingsViewModel ShortcutsSettings { get; }

    public HighlightFilterRulesViewModel HighlightFilterSettings { get; }

    public ObservableCollection<HighlightFilterRule> HighlightFilterRules { get; } = [];

    public ObservableCollection<HighlightFilterRuleProject> HighlightRuleProjects { get; } = [];

    private static string HighlightFilterRulesFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DuCom",
        "highlight-filter-rules.json");

    public ObservableCollection<PortItemViewModel> AvailablePorts { get; } = [];

    internal IReadOnlyList<string> DiscoveredPortNames => _discoveredPortNames;

    public ObservableCollection<SessionViewModel> Sessions { get; } = [];

    public bool HasSessions => Sessions.Count > 0;

    public ObservableCollection<SessionViewModel> RightSessions { get; } = [];

    [ObservableProperty]
    public partial SessionViewModel? SelectedRightSession { get; set; }

    public bool IsSplitView => RightSessions.Count > 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenCommand))]
    public partial PortItemViewModel? SelectedPortItem { get; set; }

    public string? SelectedPort => SelectedPortItem?.PortName;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CloseCommand))]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    public partial SessionViewModel? SelectedSession { get; set; }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CompositionTarget.Rendering -= OnCompositionRendering;
        _settingsSaveTimer.Stop();
        _settingsSaveTimer.Tick -= OnSettingsSaveTick;
        _portSettingsApplyTimer.Stop();
        _portSettingsApplyTimer.Tick -= OnPortSettingsApplyTick;
        SaveSettings();
        await CommandRunner.DisposeAsync();
        await Telnet.DisposeAsync();
        Watchdog.Dispose();
        VariableMonitor.Dispose();
        SessionProbes.Dispose();
        if (_privateMemoryMonitor is not null)
        {
            _privateMemoryMonitor.Sampled -= OnPrivateMemorySampled;
            _privateMemoryMonitor.ThresholdReached -= OnPrivateMemoryThresholdReached;
            await _privateMemoryMonitor.DisposeAsync();
            _privateMemoryMonitor = null;
        }
        _floatSendWindows.CloseAll();
        _logFilterWindows.CloseAll();
        Sessions.CollectionChanged -= OnSessionsChanged;
        RightSessions.CollectionChanged -= OnRightSessionsChanged;
        foreach (SessionViewModel session in Sessions)
        {
            await session.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }

    private static string GetResourceString(string key) =>
        Application.Current.TryFindResource(key) as string ?? key;
}

public enum PortSortMode
{
    NameAscending,
    NameDescending,
    ConnectedFirst,
}

public enum SplitLayoutOrientation
{
    Vertical,
    Horizontal,
}
