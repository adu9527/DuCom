using System.Collections.ObjectModel;
using System.IO;
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

public partial class MainViewModel : ApplicationSettingsViewModel, IAsyncDisposable
{
    private const string GitHubRepositoryUrl = "https://github.com/adu9527/DuCom";
    private const string ChineseUserManualUrl = "https://github.com/adu9527/DuCom/blob/main/Doc/UserManual.zh-CN.md";
    private const string EnglishUserManualUrl = "https://github.com/adu9527/DuCom/blob/main/Doc/UserManual.en-US.md";
    private readonly IPortDiscovery _portDiscovery;
    private readonly PortRefreshCoordinator _portRefreshCoordinator;
    private static readonly TimeSpan SlowOperationThreshold = TimeSpan.FromMilliseconds(50);
    private readonly HashSet<string> _hiddenPorts = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _settingsSaveTimer;
    private string[] _discoveredPortNames = [];
    private IReadOnlyDictionary<string, DiscoveredPort> _discoveredPortDetails =
        new Dictionary<string, DiscoveredPort>(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;
    private bool _isLoadingSettings;
    private readonly SettingsSaveGate _settingsSaveGate = new();
    private bool _allowSerialParametersWindowClose;
    private SettingsWindow? _settingsWindow;
    private SettingsWindow? _serialParametersWindow;
    private string[] _commandTargetPortNames = [];
    private FramePacerState _framePacerState;
    private long _nextSlowProjectionLogTimestamp;
    private readonly AnalysisWindowPreferencesService _analysisWindowPreferences = new();

    internal MainViewModel(
        IPortDiscovery portDiscovery,
        Func<WorkspaceSessionOptions, IWorkspaceSession> sessionFactory)
    {
        _portDiscovery = portDiscovery ?? throw new ArgumentNullException(nameof(portDiscovery));
        ArgumentNullException.ThrowIfNull(sessionFactory);
        SettingChanged += OnApplicationSettingChanged;
        _portRefreshCoordinator = new PortRefreshCoordinator(
            DiscoverPortsAsync,
            ApplyDiscoveredPorts,
            () => _disposed,
            (elapsed, portCount) => Program.DiagnosticLog?.Warning(
                $"Slow serial-port discovery. ElapsedMs={elapsed.TotalMilliseconds:0.0}; Ports={portCount}"),
            exception => Program.DiagnosticLog?.Warning("Serial-port discovery failed.", exception),
            SlowOperationThreshold);
        CompositionTarget.Rendering += OnCompositionRendering;
        _settingsSaveTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _settingsSaveTimer.Tick += OnSettingsSaveTick;
        Workspace = new SessionWorkspaceViewModel(new SessionWorkspaceCallbacks(
            CaptureApplicationSessionDefaults,
            sessionFactory,
            () => HighlightRuleProjects,
            portName => AvailablePorts.Any(item => string.Equals(item.PortName, portName, StringComparison.OrdinalIgnoreCase)),
            SelectPort,
            () => _portRefreshCoordinator.WaitForCurrentRefreshAsync(),
            () => CommandRunner?.StopAsync() ?? Task.CompletedTask,
            MarkSettingsDirty,
            () => SerialParameters?.NotifyActiveSessionChanged(),
            () => _sendHistoryNavigator?.Reset(),
            text => _sendHistory.Record(text),
            PersistSendHistory,
            portName => { CloseFloatSendFor(portName); CloseLogFilterFor(portName); },
            sessionId => PluginSystem?.Environment.NotifySessionClosed(sessionId),
            message => StatusMessage = message,
            GetResourceString,
            () => ReceiveMode = ReceiveMode == ReceiveDisplayMode.Str ? ReceiveDisplayMode.Hex : ReceiveDisplayMode.Str,
            () => TimestampEnabled = !TimestampEnabled,
            PrepareSessionCloseAsync,
            NotifyOpenCommandState));
        SerialParameters = new SerialParametersEditorViewModel(new SerialParametersEditorCallbacks(
            GetDefaultSerialSettings,
            ApplyDefaultSerialSettings,
            value => DefaultSendMode = value,
            value => DefaultNewline = value,
            () => Workspace.ActiveSession,
            (session, settings) =>
            {
                if (settings is null)
                {
                    Workspace.RememberPortOverride(session.PortName);
                }
                else
                {
                    Workspace.RememberPortOverride(session.PortName, settings);
                }
            },
            key => StatusMessage = string.IsNullOrEmpty(key) ? string.Empty : GetResourceString(key),
            (message, exception) =>
            {
                if (exception is null)
                {
                    Program.DiagnosticLog?.Information(message);
                }
                else
                {
                    Program.DiagnosticLog?.Error(message, exception);
                }
            }));
        ShortcutManager = new ShortcutManager();
        ShortcutManager.RegisterDefaultActions();
        ShortcutsSettings = new ShortcutsSettingsViewModel(ShortcutManager);
        HighlightFilterSettings = new HighlightFilterRulesViewModel(
            new HighlightFilterRuleService(HighlightFilterRulesFilePath));
        HighlightFilterSettings.Saved += (_, _) => LoadHighlightFilterRules();
        HighlightFilterSettings.Applied += OnHighlightRulesApplied;
        HighlightFilterSettings.ProjectsChanged += OnHighlightRuleProjectsChanged;
        _sendHistoryNavigator = new SendHistoryNavigator(_sendHistory);
        SessionProbes = new Services.SessionProbeProvider(Workspace.Sessions);
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
        string[] normalized = ConfigurationSnapshotNormalizer.NormalizePortNames(portNames);
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

    public SerialParametersEditorViewModel SerialParameters { get; }

    public SessionWorkspaceViewModel Workspace { get; }

    public ObservableCollection<HighlightFilterRule> HighlightFilterRules { get; } = [];

    public ObservableCollection<HighlightFilterRuleProject> HighlightRuleProjects { get; } = [];

    private static string HighlightFilterRulesFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DuCom",
        "highlight-filter-rules.json");

    private static string LogAnalyzerRulesFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DuCom",
        "log-analyzer-rules.json");

    public ObservableCollection<PortItemViewModel> AvailablePorts { get; } = [];

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = string.Empty;

    internal IReadOnlyList<string> DiscoveredPortNames => _discoveredPortNames;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenCommand))]
    public partial PortItemViewModel? SelectedPortItem { get; set; }

    public string? SelectedPort => SelectedPortItem?.PortName;

    partial void OnSelectedPortItemChanged(PortItemViewModel? value)
    {
        OnPropertyChanged(nameof(LogFileNamePreview));
        Workspace.SelectPort(value?.PortName);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        SettingChanged -= OnApplicationSettingChanged;
        CompositionTarget.Rendering -= OnCompositionRendering;
        _settingsSaveTimer.Stop();
        _settingsSaveTimer.Tick -= OnSettingsSaveTick;
        if (PluginSystem is { } pluginSystem)
        {
            await pluginSystem.Service.StopAllAsync();
        }
        if (!await SerialParameters.FlushAsync())
        {
            Program.DiagnosticLog?.Warning("Pending serial settings could not be flushed during shutdown.");
        }
        SerialParameters.Dispose();
        SaveSettings();
        await CommandRunner.DisposeAsync();
        await Telnet.DisposeAsync();
        _protocolDecoderWindow?.Close();
        _variablePlotWindow?.Close();
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
        _logAnalyzerWindow?.Close();
        await Workspace.DisposeAsync();

        GC.SuppressFinalize(this);
    }

    private static string GetResourceString(string key) =>
        Application.Current.TryFindResource(key) as string ?? key;

    private ApplicationSessionDefaults CaptureApplicationSessionDefaults() => new(
        BaudRate, DataBits, StopBits, Parity, Handshake, EncodingName, ReceiveMode,
        TimestampEnabled, LoggingEnabled, LogDirectory, LogRotationMegabytes,
        LogRotationEnabled, DisplayBudgetMegabytes, LogFileNameFormat, SendPrefixEnabled,
        SendPrefix, TimestampFormat, DefaultSendMode, DefaultNewline, FreezeAfterSend);

    private void SelectPort(string? portName)
    {
        if (string.IsNullOrWhiteSpace(portName))
        {
            return;
        }

        SelectedPortItem = AvailablePorts.FirstOrDefault(item =>
            string.Equals(item.PortName, portName, StringComparison.OrdinalIgnoreCase));
    }
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
