using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DuCom.Core.Ports;
using DuCom.Core.Parsing;
using DuCom.Core.Sending;
using DuCom.Core.Diagnostics;
using DuCom.Services;
using DuCom.Services.Shortcuts;

namespace DuCom.ViewModels;

public partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private static readonly int[] DefaultBaudRates = [9_600, 19_200, 115_200, 921_600, 1_152_000, 1_500_000, 2_000_000, 3_000_000];

    private static readonly JsonSerializerOptions ConfigurationJsonOptions = new() { WriteIndented = true };
    private readonly Func<WorkspaceSessionOptions, IWorkspaceSession> _sessionFactory;
    private readonly IPortDiscovery _portDiscovery;
    private static readonly TimeSpan MinimumRenderInterval = TimeSpan.FromSeconds(1d / 60d);
    private static readonly TimeSpan StatusRefreshInterval = TimeSpan.FromMilliseconds(100);
    private readonly HashSet<string> _hiddenPorts = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _settingsSaveTimer;
    private readonly DispatcherTimer _portSettingsApplyTimer;
    private readonly DispatcherTimer _backgroundImageTimer;
    private readonly SendHistory _sendHistory = new();
    private readonly SendHistoryNavigator _sendHistoryNavigator;
    private string[] _discoveredPortNames = [];
    private IReadOnlyDictionary<string, DiscoveredPort> _discoveredPortDetails =
        new Dictionary<string, DiscoveredPort>(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;
    private bool _isLoadingSettings;
    private bool _settingsDirty;
    private bool _portSettingsApplyPending;
    private bool _allowSerialParametersWindowClose;
    private Services.PrivateMemoryMonitorService? _privateMemoryMonitor;
    private bool _privateMemoryThresholdWasReached;
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
    private string[] _backgroundImagePlaylist = [];
    private int _backgroundImageIndex = -1;

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
        _backgroundImageTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(30),
        };
        _backgroundImageTimer.Tick += OnBackgroundImageTimerTick;
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
        PluginManager = new PluginManagerViewModel(this);
        LoadHighlightFilterRules();
        SendHistoryFileService.LoadInto(_sendHistory);
        SyncAppearanceSelection();
        RefreshPorts();
        ApplySystemBehaviorSettings();
    }

    internal async Task RestorePersistedSessionsAsync()
    {
        if (_sessionsRestored)
        {
            return;
        }

        _sessionsRestored = true;
        string[] openPorts = ResolvePersistedOpenSessionPorts();
        if (openPorts.Length == 0)
        {
            return;
        }

        _isLoadingSettings = true;
        try
        {
            foreach (string portName in openPorts)
            {
                if (!AvailablePorts.Any(item => string.Equals(item.PortName, portName, StringComparison.OrdinalIgnoreCase)))
                {
                    Program.DiagnosticLog?.Warning($"Persisted session port is not currently available. Port={portName}");
                    continue;
                }

                SessionViewModel? session = await EnsureSessionOpenAsync(portName);
                if (session is null)
                {
                    Program.DiagnosticLog?.Warning($"Persisted session could not be reopened. Port={portName}");
                }
            }

            HashSet<string> rightPorts = new(_persistedRightPanePorts, StringComparer.OrdinalIgnoreCase);
            SessionViewModel? leftSession = Sessions.FirstOrDefault(session =>
                    session.IsOpen &&
                    !rightPorts.Contains(session.PortName) &&
                    string.Equals(session.PortName, _persistedSelectedSessionPort, StringComparison.OrdinalIgnoreCase))
                ?? Sessions.FirstOrDefault(session => session.IsOpen && !rightPorts.Contains(session.PortName));
            if (leftSession is null)
            {
                leftSession = Sessions.FirstOrDefault(session => session.IsOpen);
                rightPorts.Clear();
            }

            foreach (SessionViewModel session in Sessions.Where(item =>
                         item.IsOpen && rightPorts.Contains(item.PortName) && !ReferenceEquals(item, leftSession)))
            {
                session.IsInRightPane = true;
                if (!RightSessions.Contains(session))
                {
                    RightSessions.Add(session);
                }
            }

            SelectedRightSession = FindOpenSession(_persistedSelectedRightSessionPort, rightPane: true)
                ?? RightSessions.FirstOrDefault(item => item.IsOpen);
            SelectedSession = leftSession;
            if (SelectedSession is not null)
            {
                SelectedPortItem = AvailablePorts.FirstOrDefault(item =>
                    string.Equals(item.PortName, SelectedSession.PortName, StringComparison.OrdinalIgnoreCase));
            }
        }
        finally
        {
            _isLoadingSettings = false;
            NotifyCommandStates();
        }
    }

    private string[] ResolvePersistedOpenSessionPorts()
    {
        IEnumerable<string> ports = _persistedOpenSessionPorts.Count > 0
            ? _persistedOpenSessionPorts
            : _persistedRightPanePorts.Concat(_persistedSessionOrder.Where(portName =>
                !_persistedRightPanePorts.Contains(portName, StringComparer.OrdinalIgnoreCase)).Take(1));
        return [.. ports
            .Where(portName => !string.IsNullOrWhiteSpace(portName))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private SessionViewModel? FindOpenSession(string? portName, bool rightPane)
    {
        if (string.IsNullOrWhiteSpace(portName))
        {
            return null;
        }

        return Sessions.FirstOrDefault(session =>
            session.IsOpen &&
            session.IsInRightPane == rightPane &&
            string.Equals(session.PortName, portName, StringComparison.OrdinalIgnoreCase));
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

    internal bool HasPrivateMemoryMonitor => _privateMemoryMonitor is not null;

    internal void AttachPrivateMemoryMonitor(Services.PrivateMemoryMonitorService service)
    {
        if (_privateMemoryMonitor is not null)
        {
            throw new InvalidOperationException("The private-memory monitor is already attached.");
        }

        _privateMemoryMonitor = service ?? throw new ArgumentNullException(nameof(service));
        _privateMemoryMonitor.Sampled += OnPrivateMemorySampled;
        _privateMemoryMonitor.ThresholdReached += OnPrivateMemoryThresholdReached;
    }

    private readonly DuCom.Core.Presenting.PortWindowRegistry<FloatSendWindow> _floatSendWindows = new(
        window =>
        {
            if (window.IsLoaded)
            {
                window.Activate();
            }
        },
        window => window.Close());

    private readonly DuCom.Core.Presenting.PortWindowRegistry<LogFilterWindow> _logFilterWindows = new(
        window =>
        {
            if (window.IsLoaded)
            {
                window.Activate();
            }
        },
        window => window.Close());

    [RelayCommand]
    private void ToggleFloatSend()
    {
        SessionViewModel? session = SelectedSession ?? SelectedRightSession;
        if (session is null)
        {
            StatusMessage = GetResourceString("Status.NoSessionSelected");
            return;
        }

        _floatSendWindows.GetOrOpen(session.PortName, _ =>
        {
            FloatSendWindow window = new(session) { Owner = Application.Current.MainWindow };
            window.Show();
            Program.DiagnosticLog?.Information($"Float send window opened. Port={session.PortName}");
            return window;
        });
    }

    [RelayCommand]
    private void ShowLogFilter(SessionViewModel? session)
    {
        session ??= SelectedSession ?? SelectedRightSession;
        if (session is null)
        {
            StatusMessage = GetResourceString("Status.NoSessionSelected");
            return;
        }

        _logFilterWindows.GetOrOpen(session.PortName, _ =>
        {
            LogFilterWindow window = new(session) { Owner = Application.Current.MainWindow };
            window.Show();
            Program.DiagnosticLog?.Information($"Log filter window opened. Port={session.PortName}");
            return window;
        });
    }

    internal void CloseFloatSendFor(string portName) => _floatSendWindows.Close(portName);

    internal void CloseLogFilterFor(string portName) => _logFilterWindows.Close(portName);

    /// <summary>Called by the window layer when the user closes a float send window directly.</summary>
    public void FloatSendClosedFromWindow(string portName) => _floatSendWindows.Remove(portName);

    /// <summary>Called by the window layer when the user closes a log filter window directly.</summary>
    public void LogFilterClosedFromWindow(string portName) => _logFilterWindows.Remove(portName);

    /// <summary>
    /// Applies a reply-window duration edited in one float send window to every other open
    /// float send window, matching the reference tool's behavior.
    /// </summary>
    internal void ApplyReplyWindowToFloatSends(FloatSendWindow source, int milliseconds)
    {
        foreach (FloatSendWindow window in _floatSendWindows.Windows)
        {
            if (!ReferenceEquals(window, source))
            {
                window.SetReplyWindowMs(milliseconds);
            }
        }
    }

    public ShortcutManager ShortcutManager { get; }

    /// <summary>Shortcuts management hosted in the settings window; shares ShortcutManager.</summary>
    public ShortcutsSettingsViewModel ShortcutsSettings { get; }

    public HighlightFilterRulesViewModel HighlightFilterSettings { get; }

    public PluginManagerViewModel PluginManager { get; }

    public ObservableCollection<HighlightFilterRule> HighlightFilterRules { get; } = [];

    public ObservableCollection<HighlightFilterRuleProject> HighlightRuleProjects { get; } = [];

    private static string ShortcutsFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DuCom",
        "shortcuts.json");

    private static string HighlightFilterRulesFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DuCom",
        "highlight-filter-rules.json");

    private void LoadShortcuts()
    {
        if (ShortcutManager.TryLoad(ShortcutsFilePath))
        {
            return;
        }

        Program.DiagnosticLog?.Warning($"Failed to load shortcuts from {ShortcutsFilePath}; using defaults.");
    }

    public ObservableCollection<int> BaudRates { get; } = [.. DefaultBaudRates];

    public IReadOnlyList<int> DataBitsOptions { get; } = [5, 6, 7, 8];

    public IReadOnlyList<StopBits> StopBitsOptions { get; } = [StopBits.One, StopBits.Two, StopBits.OnePointFive];

    public IReadOnlyList<Parity> ParityOptions { get; } = Enum.GetValues<Parity>();

    public IReadOnlyList<Handshake> HandshakeOptions { get; } = Enum.GetValues<Handshake>();

    public IReadOnlyList<string> EncodingOptions { get; } = [Encoding.UTF8.WebName, Encoding.ASCII.WebName, "gb2312", "gbk"];

    public IReadOnlyList<ReceiveDisplayMode> ReceiveModeOptions { get; } = Enum.GetValues<ReceiveDisplayMode>();

    public ObservableCollection<PortItemViewModel> AvailablePorts { get; } = [];

    internal IReadOnlyList<string> DiscoveredPortNames => _discoveredPortNames;

    public ObservableCollection<SessionViewModel> Sessions { get; } = [];

    public bool HasSessions => Sessions.Count > 0;

    [ObservableProperty]
    public partial int BaudRate { get; set; } = 1_152_000;

    [ObservableProperty]
    public partial int DataBits { get; set; } = 8;

    [ObservableProperty]
    public partial StopBits StopBits { get; set; } = StopBits.One;

    [ObservableProperty]
    public partial Parity Parity { get; set; } = Parity.None;

    [ObservableProperty]
    public partial Handshake Handshake { get; set; } = Handshake.None;

    [ObservableProperty]
    public partial string EncodingName { get; set; } = Encoding.UTF8.WebName;

    [ObservableProperty]
    public partial ReceiveDisplayMode ReceiveMode { get; set; } = ReceiveDisplayMode.Str;

    [ObservableProperty]
    public partial bool TimestampEnabled { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimestampPreview))]
    public partial string TimestampFormat { get; set; } = "HH:mm:ss.fff";

    public IReadOnlyList<string> TimestampFormatOptions { get; } =
        ["HH:mm:ss", "HH:mm:ss.fff", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss.fff"];

    public string TimestampPreview => $"[{DateTimeOffset.Now.ToString(TimestampFormat, CultureInfo.InvariantCulture)}] Device boot complete";

    [ObservableProperty]
    public partial bool LoggingEnabled { get; set; } = true;

    private SettingsWindow? _settingsWindow;
    private SettingsWindow? _serialParametersWindow;
    private SessionViewModel? _portSettingsTargetSession;

    [ObservableProperty]
    public partial bool IsSidebarVisible { get; set; } = true;

    [ObservableProperty]
    public partial bool IsChineseLanguage { get; private set; }

    [ObservableProperty]
    public partial bool IsEnglishLanguage { get; private set; }

    [ObservableProperty]
    public partial bool IsSystemTheme { get; private set; }

    [ObservableProperty]
    public partial bool IsLightTheme { get; private set; }

    [ObservableProperty]
    public partial bool IsDarkTheme { get; private set; }

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool ShowHiddenPorts { get; set; }

    [ObservableProperty]
    public partial bool ShowSerialPorts { get; set; } = true;

    [ObservableProperty]
    public partial bool ShowVirtualPorts { get; set; } = true;

    [ObservableProperty]
    public partial PortSortMode PortSortMode { get; set; } = PortSortMode.NameAscending;

    [ObservableProperty]
    public partial bool WordWrap { get; set; }

    [ObservableProperty]
    public partial bool ShowLineNumbers { get; set; }

    [ObservableProperty]
    public partial bool HighlightCurrentLine { get; set; } = true;

    [ObservableProperty]
    public partial bool ShowControlCharacters { get; set; }

    [ObservableProperty]
    public partial bool ShowSpaces { get; set; }

    [ObservableProperty]
    public partial bool ShowTabs { get; set; }

    [ObservableProperty]
    public partial double LogFontSize { get; set; } = 14;

    [ObservableProperty]
    public partial string LogFontFamily { get; set; } = "Cascadia Mono";

    public IReadOnlyList<string> LogFontFamilies { get; } = [.. Fonts.SystemFontFamilies
        .Select(font => font.Source)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Order(StringComparer.OrdinalIgnoreCase)];

    public ObservableCollection<SessionViewModel> RightSessions { get; } = [];

    [ObservableProperty]
    public partial SessionViewModel? SelectedRightSession { get; set; }

    public bool IsSplitView => RightSessions.Count > 0;

    [ObservableProperty]
    public partial SplitLayoutOrientation SplitOrientation { get; set; } = SplitLayoutOrientation.Vertical;

    [ObservableProperty]
    public partial double SplitterRatio { get; set; } = 0.5d;

    public bool IsSerialParametersEditable => _portSettingsTargetSession is { IsBusy: false };

    public bool IsEditingPortSettings => _portSettingsTargetSession is not null;

    [ObservableProperty]
    public partial SendMode DefaultSendMode { get; set; } = SendMode.Str;

    [ObservableProperty]
    public partial NewlinePolicy DefaultNewline { get; set; } = NewlinePolicy.None;

    [ObservableProperty]
    public partial SendMode SerialParameterSendMode { get; set; } = SendMode.Str;

    [ObservableProperty]
    public partial NewlinePolicy SerialParameterNewline { get; set; } = NewlinePolicy.None;

    [ObservableProperty]
    public partial bool SerialParameterInterpretSendEscapes { get; set; }

    [ObservableProperty]
    public partial bool SerialParameterTimedSendEnabled { get; set; }

    [ObservableProperty]
    public partial int SerialParameterTimedSendIntervalMilliseconds { get; set; } = 1000;

    [ObservableProperty]
    public partial int SerialParameterBaudRate { get; set; } = 1_152_000;

    [ObservableProperty]
    public partial int SerialParameterDataBits { get; set; } = 8;

    [ObservableProperty]
    public partial StopBits SerialParameterStopBits { get; set; } = StopBits.One;

    [ObservableProperty]
    public partial Parity SerialParameterParity { get; set; } = Parity.None;

    [ObservableProperty]
    public partial Handshake SerialParameterHandshake { get; set; } = Handshake.None;

    [ObservableProperty]
    public partial string SerialParameterEncodingName { get; set; } = Encoding.UTF8.WebName;

    [ObservableProperty]
    public partial bool SerialParameterDtrEnable { get; set; }

    [ObservableProperty]
    public partial bool SerialParameterRtsEnable { get; set; }

    [ObservableProperty]
    public partial bool SerialParameterDiscardNull { get; set; }

    [ObservableProperty]
    public partial bool SerialParameterAutoReconnect { get; set; }

    [ObservableProperty]
    public partial ReceiveDisplayMode SerialParameterReceiveMode { get; set; } = ReceiveDisplayMode.Str;

    [ObservableProperty]
    public partial bool SerialParameterTimestampEnabled { get; set; } = true;

    [ObservableProperty]
    public partial bool SerialParameterLoggingEnabled { get; set; } = true;

    [ObservableProperty]
    public partial bool SerialParameterFollowEnd { get; set; } = true;

    [ObservableProperty]
    public partial bool SerialParameterFilterEnabled { get; set; } = true;

    [ObservableProperty]
    public partial string LogDirectory { get; set; } = GetDefaultLogDirectory();

    [ObservableProperty]
    public partial int LogRotationMegabytes { get; set; } = 40;

    [ObservableProperty]
    public partial bool LogRotationEnabled { get; set; } = true;

    [ObservableProperty]
    public partial int DisplayBudgetMegabytes { get; set; } = 64;

    [ObservableProperty]
    public partial bool PrivateMemoryMonitorEnabled { get; set; }

    [ObservableProperty]
    public partial int PrivateMemoryThresholdMiB { get; set; } = 1024;

    [ObservableProperty]
    public partial long PrivateMemoryBytes { get; private set; }

    [ObservableProperty]
    public partial bool IsPrivateMemoryThresholdReached { get; private set; }

    [ObservableProperty]
    public partial string LogFileNameFormat { get; set; } = "{Port}-{yyyy}-{MM}-{dd} {HH}-{mm}-{ss}.{fff}";

    public string LogFileNamePreview => PreviewLogFileName(LogFileNameFormat, SelectedSession?.PortName ?? SelectedPort ?? "COM31");

    [ObservableProperty]
    public partial bool FreezeAfterSend { get; set; }

    [ObservableProperty]
    public partial bool SendPrefixEnabled { get; set; } = true;

    [ObservableProperty]
    public partial string SendPrefix { get; set; } = "TX > ";

    [ObservableProperty]
    public partial bool PauseFollowOnMouseWheel { get; set; } = true;

    [ObservableProperty]
    public partial bool PauseFollowOnFocus { get; set; }

    [ObservableProperty]
    public partial bool ShowPauseHint { get; set; } = true;

    [ObservableProperty]
    public partial bool AutoBackupEnabled { get; set; } = true;

    [ObservableProperty]
    public partial int AutoBackupPeriodDays { get; set; } = 7;

    [ObservableProperty]
    public partial bool PreventSleep { get; set; }

    [ObservableProperty]
    public partial bool CloseToTaskbar { get; set; }

    [ObservableProperty]
    public partial int NewBaudRate { get; set; }

    [ObservableProperty]
    public partial bool ShowPortType { get; set; } = true;

    [ObservableProperty]
    public partial double SearchOpacity { get; set; } = 1d;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BackgroundImageSource))]
    public partial bool BackgroundImageEnabled { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BackgroundImageSource))]
    public partial string BackgroundImagePath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string BackgroundImageFolderPath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial BackgroundImagePlaybackMode BackgroundImagePlaybackMode { get; set; }

    [ObservableProperty]
    public partial int BackgroundImageIntervalSeconds { get; set; } = 300;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BackgroundImageSource))]
    public partial string CurrentBackgroundImagePath { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial double BackgroundImageOpacity { get; set; } = 0.18d;

    public ImageSource? BackgroundImageSource
    {
        get
        {
            string path = BackgroundImagePlaybackMode == BackgroundImagePlaybackMode.SingleImage
                ? BackgroundImagePath
                : CurrentBackgroundImagePath;
            if (!BackgroundImageEnabled || string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return null;
            }

            try
            {
                BitmapImage image = new();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.UriSource = new Uri(path, UriKind.Absolute);
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch
            {
                return null;
            }
        }
    }

    [ObservableProperty]
    public partial int TelnetPort { get; set; } = 23;

    [ObservableProperty]
    public partial bool TelnetAllowRemote { get; set; }

    [ObservableProperty]
    public partial bool TelnetAuthenticationEnabled { get; set; }

    [ObservableProperty]
    public partial string TelnetUsername { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenCommand))]
    public partial PortItemViewModel? SelectedPortItem { get; set; }

    public string? SelectedPort => SelectedPortItem?.PortName;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CloseCommand))]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    public partial SessionViewModel? SelectedSession { get; set; }

    [RelayCommand]
    private void RefreshPorts()
    {
        string? previous = SelectedPort;
        _discoveredPortDetails = (_portDiscovery as IPortDetailsProvider)?.GetPortDetails()
            ?? new Dictionary<string, DiscoveredPort>(StringComparer.OrdinalIgnoreCase);
        _discoveredPortNames = _discoveredPortDetails.Count > 0
            ? _discoveredPortDetails.Keys.ToArray()
            : _portDiscovery.GetPortNames().ToArray();
        RebuildPortItems(previous);
        CloseSessionsForRemovedPorts();
        ReconnectReturnedPorts();
    }

    private void CloseSessionsForRemovedPorts()
    {
        HashSet<string> discovered = new(_discoveredPortNames, StringComparer.OrdinalIgnoreCase);
        foreach (SessionViewModel session in Sessions.Where(session => session.IsOpen && !discovered.Contains(session.PortName)).ToArray())
        {
            session.MarkDeviceRemoved();
            _ = CloseRemovedPortSessionAsync(session);
        }
    }

    private void ReconnectReturnedPorts()
    {
        HashSet<string> discovered = new(_discoveredPortNames, StringComparer.OrdinalIgnoreCase);
        foreach (SessionViewModel session in Sessions.Where(session => session.IsWaitingForReconnect && discovered.Contains(session.PortName)).ToArray())
        {
            _ = ReconnectReturnedPortAsync(session);
        }
    }

    private async Task ReconnectReturnedPortAsync(SessionViewModel session)
    {
        if (!session.IsWaitingForReconnect || session.IsBusy || session.IsOpen)
        {
            return;
        }

        session.ClearReconnectWait();
        try
        {
            SessionViewModel replacement = await RebuildClosedSessionAsync(session);
            replacement.AutoReconnect = true;
            PortCommandResult result = await replacement.OpenAsync();
            Program.DiagnosticLog?.Information($"Automatic reconnect completed. Port={replacement.PortName}; Result={result}");
        }
        catch (Exception exception)
        {
            Program.DiagnosticLog?.Warning($"Automatic reconnect failed. Port={session.PortName}; {exception.Message}");
        }
    }

    private static async Task CloseRemovedPortSessionAsync(SessionViewModel session)
    {
        try
        {
            Program.DiagnosticLog?.Warning($"Connected serial port disappeared from discovery; closing its session. Port={session.PortName}");
            await session.CloseAsync();
        }
        catch (Exception exception)
        {
            Program.DiagnosticLog?.Error($"Failed to close removed serial-port session. Port={session.PortName}", exception);
        }
    }

    private void RebuildPortItems(string? selectedPort = null)
    {
        IEnumerable<string> names = PortSortMode switch
        {
            PortSortMode.NameDescending => _discoveredPortNames.OrderByDescending(name => name, StringComparer.OrdinalIgnoreCase),
            PortSortMode.ConnectedFirst => _discoveredPortNames
                .OrderByDescending(name => Sessions.Any(session => session.IsOpen && string.Equals(session.PortName, name, StringComparison.OrdinalIgnoreCase)))
                .ThenBy(name => name, StringComparer.OrdinalIgnoreCase),
            _ => _discoveredPortNames.Order(StringComparer.OrdinalIgnoreCase),
        };
        AvailablePorts.Clear();
        foreach (string name in names)
        {
            bool hidden = _hiddenPorts.Contains(name);
            _discoveredPortDetails.TryGetValue(name, out DiscoveredPort? detail);
            bool isVirtual = detail?.Type == DiscoveredPortType.Virtual;
            bool typeVisible = isVirtual ? ShowVirtualPorts : ShowSerialPorts;
            if (typeVisible && (!hidden || ShowHiddenPorts))
            {
                string type = detail?.Type switch
                {
                    DiscoveredPortType.Virtual => "VAR",
                    DiscoveredPortType.UsbSerial => "USB",
                    _ => "COM",
                };
                AvailablePorts.Add(new PortItemViewModel(
                    name,
                    TogglePortAsync,
                    TogglePortHidden,
                    type,
                    detail?.Description ?? string.Empty,
                    detail?.DeviceName ?? name,
                    detail?.Manufacturer ?? string.Empty,
                    detail?.VidPid ?? string.Empty,
                    detail?.SerialNumber ?? string.Empty,
                    detail?.DeviceInstanceId ?? string.Empty,
                    detail?.LocationInfo ?? string.Empty)
                {
                    IsHidden = hidden,
                });
            }
        }

        SelectedPortItem = selectedPort is not null
            ? AvailablePorts.FirstOrDefault(item => string.Equals(item.PortName, selectedPort, StringComparison.OrdinalIgnoreCase))
            : AvailablePorts.FirstOrDefault();
    }

    [RelayCommand]
    private void ShowVisiblePorts()
    {
        ShowHiddenPorts = false;
        RebuildPortItems(SelectedPort);
    }

    [RelayCommand]
    private void ShowAllPorts()
    {
        ShowHiddenPorts = true;
        RebuildPortItems(SelectedPort);
    }

    [RelayCommand]
    private void RestoreAllHiddenPorts()
    {
        _hiddenPorts.Clear();
        ShowHiddenPorts = false;
        MarkSettingsDirty();
        RebuildPortItems(SelectedPort);
    }

    [RelayCommand]
    private void SetPortSort(string mode)
    {
        if (Enum.TryParse(mode, true, out PortSortMode parsed))
        {
            PortSortMode = parsed;
            RebuildPortItems(SelectedPort);
        }
    }

    [RelayCommand]
    private void TogglePortHidden(PortItemViewModel port)
    {
        if (port.IsHidden)
        {
            _hiddenPorts.Remove(port.PortName);
        }
        else
        {
            _hiddenPorts.Add(port.PortName);
        }

        MarkSettingsDirty(); // hidden ports are part of the persisted settings snapshot
        RebuildPortItems(SelectedPort);
    }

    [RelayCommand(CanExecute = nameof(CanOpen))]
    private async Task OpenAsync()
    {
        string portName = SelectedPort!;
        SessionViewModel? session = Sessions.FirstOrDefault(
            item => string.Equals(item.PortName, portName, StringComparison.OrdinalIgnoreCase));
        if (session is { IsOpen: false })
        {
            session = await RebuildClosedSessionAsync(session);
        }
        bool createdNew = false;
        if (session is null)
        {
            try
            {
                SerialPortSettings defaults = SerialPortSettings.Default(portName);
                SerialPortSettings settings = defaults with
                {
                    BaudRate = BaudRate,
                    DataBits = DataBits,
                    StopBits = StopBits,
                    Parity = Parity,
                    Handshake = Handshake,
                    EncodingName = EncodingName,
                    DtrEnable = false,
                    RtsEnable = false,
                    DiscardNull = false,
                };
                if (_portOverrides.TryGetValue(portName, out PortSettingSnapshot? overrideValues))
                {
                    settings = settings with
                    {
                        BaudRate = overrideValues.BaudRate,
                        DataBits = overrideValues.DataBits,
                        StopBits = overrideValues.StopBits,
                        Parity = overrideValues.Parity,
                        Handshake = overrideValues.Handshake,
                        EncodingName = overrideValues.EncodingName,
                        DtrEnable = overrideValues.DtrEnable ?? settings.DtrEnable,
                        RtsEnable = overrideValues.RtsEnable ?? settings.RtsEnable,
                        DiscardNull = overrideValues.DiscardNull ?? settings.DiscardNull,
                    };
                }
                PortSessionPreferences preferences = ResolvePortPreferences(portName);
                session = CreateSession(settings, preferences);
                session.AutoReconnect = overrideValues?.AutoReconnect ?? false;
                Sessions.Add(session);
                createdNew = true;
                Program.DiagnosticLog?.Information(
                    $"Session created. Port={portName}; Baud={settings.BaudRate}; DataBits={settings.DataBits}; StopBits={settings.StopBits}; Parity={settings.Parity}; Handshake={settings.Handshake}; Encoding={settings.EncodingName}; ReceiveMode={preferences.ReceiveMode}; Timestamp={preferences.TimestampEnabled}; Logging={preferences.LoggingEnabled}; LogDirectory={preferences.LogDirectory}");
            }
            catch (Exception exception)
            {
                Program.DiagnosticLog?.Error($"Invalid port settings. Port={portName}; {exception.Message}");
                StatusMessage = GetResourceString("Status.InvalidPortSettings");
                return;
            }
        }

        SelectedSession = session;
        PortCommandResult result = await session.OpenAsync();
        if (result != PortCommandResult.Succeeded && createdNew)
        {
            Sessions.Remove(session);
            CloseFloatSendFor(session.PortName); CloseLogFilterFor(session.PortName);
            await session.DisposeAsync();
            SelectedSession = Sessions.FirstOrDefault();
            StatusMessage = GetResourceString("Status.OpenFailed")
                .Replace("{0}", session.FaultMessage, StringComparison.Ordinal);
            Program.DiagnosticLog?.Warning(
                $"Open failed and new session removed. Port={portName}; Result={result}; Fault={session.FaultMessage}");
        }
        else
        {
            StatusMessage = string.Empty;
            if (session.IsOpen)
            {
                RememberPortOverride(portName);
            }

            Program.DiagnosticLog?.Information(
                $"Open command completed. Port={portName}; Result={result}; IsOpen={session.IsOpen}; Fault={session.FaultMessage}");
        }

        NotifyCommandStates();
    }

    private bool CanOpen() => !string.IsNullOrWhiteSpace(SelectedPort);

    [RelayCommand(CanExecute = nameof(CanClose))]
    private async Task CloseAsync()
    {
        await SelectedSession!.CloseAsync();
        Program.DiagnosticLog?.Information(
            $"Close command completed. Port={SelectedSession.PortName}; IsOpen={SelectedSession.IsOpen}; Fault={SelectedSession.FaultMessage}");
        NotifyCommandStates();
    }

    private bool CanClose() => SelectedSession is { IsOpen: true, IsBusy: false };

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        SessionViewModel session = SelectedSession!;
        try
        {
            await session.SendAsync();
            if (FreezeAfterSend)
            {
                session.FollowEnd = false;
            }
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException)
        {
            StatusMessage = GetResourceString("Status.InvalidHex")
                .Replace("{0}", exception.Message, StringComparison.Ordinal);
            Program.DiagnosticLog?.Warning($"Send rejected. Port={session.PortName}; {exception.Message}");
            return;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or TimeoutException or OperationCanceledException)
        {
            StatusMessage = GetResourceString("Status.SendFailed")
                .Replace("{0}", exception.Message, StringComparison.Ordinal);
            Program.DiagnosticLog?.Warning($"Send failed. Port={session.PortName}; {exception.Message}");
            NotifyCommandStates();
            return;
        }

        StatusMessage = string.Empty;
        if (_sendHistory.Record(session.SendText))
        {
            try
            {
                SendHistoryFileService.Save(_sendHistory);
            }
            catch (Exception exception)
            {
                Program.DiagnosticLog?.Warning($"Failed to save send history. {exception.Message}");
            }
        }

        _sendHistoryNavigator.Reset();
        NotifyCommandStates();
    }

    /// <summary>
    /// Interactive up/down history navigation for the send editor. Returns the applied text or
    /// null when nothing changed (history empty, already at boundary).
    /// </summary>
    public string? NavigateSendHistory(bool previous, string currentText)
    {
        string? applied = previous ? _sendHistoryNavigator.MovePrevious(currentText) : _sendHistoryNavigator.MoveNext();
        if (applied is not null && SelectedSession is not null)
        {
            SelectedSession.SendText = applied;
        }

        return applied;
    }

    private bool CanSend() => SelectedSession is { IsOpen: true, IsBusy: false };

    [RelayCommand]
    private void ClearDisplay() => SelectedSession?.ClearDisplay();

    [RelayCommand]
    private void FormatJson()
    {
        SessionViewModel? session = SelectedSession;
        if (session is null || string.IsNullOrWhiteSpace(session.SendText))
        {
            return;
        }

        try
        {
            using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(session.SendText);
            string formatted = JsonSerializer.Serialize(document.RootElement, ConfigurationJsonOptions);
            session.SendText = formatted;
            StatusMessage = string.Empty;
            Program.DiagnosticLog?.Information($"Formatted JSON in send editor. Port={session.PortName}.");
        }
        catch (System.Text.Json.JsonException exception)
        {
            StatusMessage = GetResourceString("Status.InvalidJson")
                .Replace("{0}", exception.Message, StringComparison.Ordinal);
            Program.DiagnosticLog?.Warning($"JSON formatting failed. Port={session.PortName}; {exception.Message}");
        }
    }

    [RelayCommand]
    private void JoinLines()
    {
        SessionViewModel? session = SelectedSession;
        if (session is null)
        {
            return;
        }

        string normalized = session.SendText
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace('\n', ' ');
        session.SendText = normalized;
    }

    [RelayCommand]
    private async Task ToggleSettingsAsync()
    {
        if (_settingsWindow is { IsLoaded: true })
        {
            _settingsWindow.Activate();
            return;
        }

        if (_serialParametersWindow is { IsLoaded: true })
        {
            if (!await FlushPortSettingsAsync())
            {
                _serialParametersWindow.Activate();
                return;
            }

            _allowSerialParametersWindowClose = true;
            _serialParametersWindow.Close();
        }

        ApplyDefaultSettingsToEditor();
        SerialParameterSendMode = DefaultSendMode;
        SerialParameterNewline = DefaultNewline;
        _settingsWindow = new SettingsWindow()
        {
            Owner = Application.Current.MainWindow,
            DataContext = this,
        };
        _settingsWindow.Closed += (_, _) =>
        {
            ShortcutsSettings.CancelPendingEdit();
            _settingsWindow = null;
        };
        _settingsWindow.Show();
    }

    [RelayCommand]
    private void OpenSerialParameters(SessionViewModel? session)
    {
        session ??= SelectedSession;
        if (session is null)
        {
            return;
        }

        if (_serialParametersWindow is { IsLoaded: true })
        {
            _serialParametersWindow.SelectCategory(1);
            _serialParametersWindow.SetWindowTitle(GetResourceString("Settings.SerialParameters"));
            _serialParametersWindow.Activate();
            return;
        }

        _portSettingsTargetSession = session;
        OnPropertyChanged(nameof(IsEditingPortSettings));
        ApplySessionSettingsToEditor(session.WorkspaceSession.Settings);
        SerialParameterReceiveMode = session.ReceiveMode;
        SerialParameterTimestampEnabled = session.TimestampEnabled;
        SerialParameterLoggingEnabled = session.LoggingEnabled;
        SerialParameterFollowEnd = session.FollowEnd;
        SerialParameterFilterEnabled = session.FilterEnabled;
        SerialParameterSendMode = session.SendMode;
        SerialParameterNewline = session.Newline;
        SerialParameterInterpretSendEscapes = session.InterpretSendEscapes;
        SerialParameterTimedSendEnabled = session.TimedSendEnabled;
        SerialParameterTimedSendIntervalMilliseconds = session.TimedSendIntervalMilliseconds;
        SerialParameterAutoReconnect = session.AutoReconnect;
        if (_settingsWindow is { IsLoaded: true })
        {
            _settingsWindow.Close();
        }

        _serialParametersWindow = new SettingsWindow(selectedCategory: 1, transportOnly: true)
        {
            Owner = Application.Current.MainWindow,
            DataContext = this,
            Title = GetResourceString("Settings.SerialParameters"),
        };
        _allowSerialParametersWindowClose = false;
        _serialParametersWindow.Closing += OnSerialParametersWindowClosing;
        _serialParametersWindow.Closed += (_, _) =>
        {
            if (_serialParametersWindow is not null)
            {
                _serialParametersWindow.Closing -= OnSerialParametersWindowClosing;
            }

            _serialParametersWindow = null;
            _portSettingsTargetSession = null;
            OnPropertyChanged(nameof(IsEditingPortSettings));
            _portSettingsApplyTimer.Stop();
            _portSettingsApplyPending = false;
            _allowSerialParametersWindowClose = false;
        };
        _serialParametersWindow.Show();
    }

    private async void OnSerialParametersWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_allowSerialParametersWindowClose)
        {
            return;
        }

        e.Cancel = true;
        if (!await FlushPortSettingsAsync())
        {
            return;
        }

        _allowSerialParametersWindowClose = true;
        if (sender is SettingsWindow window)
        {
            // Closing is still in progress after the awaited settings flush. Queue the
            // final request so WPF receives it only after this notification has returned.
            _ = window.Dispatcher.BeginInvoke(
                window.Close,
                System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
    }

    [RelayCommand]
    private void OpenSendOptions(SessionViewModel? session)
    {
        OpenSerialParameters(session);
        if (_serialParametersWindow is not { IsLoaded: true })
        {
            return;
        }

        _serialParametersWindow.SelectCategory(5);
        _serialParametersWindow.SetWindowTitle(GetResourceString("Send.Options"));
        _serialParametersWindow.Activate();
    }

    internal async Task ApplySessionBaudRateAsync(SessionViewModel session, int baudRate)
    {
        if (session.IsBusy || session.BaudRate == baudRate)
        {
            return;
        }

        SerialPortSettings updated = session.WorkspaceSession.Settings with { BaudRate = baudRate };
        await ApplyPortSettingsAsync(session, updated);
    }

    [RelayCommand]
    private void OpenLogFolder(SessionViewModel? session)
    {
        IWorkspaceSession? workspaceSession = (session ?? SelectedSession ?? SelectedRightSession)?.WorkspaceSession;
        string directory = workspaceSession?.LogDirectory ?? LogDirectory;
        Directory.CreateDirectory(directory);
        if (workspaceSession?.CurrentLogFilePath is { } currentLogFile && File.Exists(currentLogFile))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{currentLogFile}\"") { UseShellExecute = true });
            return;
        }

        Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
    }

    [RelayCommand]
    private void SelectLogDirectory()
    {
        OpenFolderDialog dialog = new()
        {
            Title = (string?)Application.Current.TryFindResource("Settings.SelectLogDirectory") ?? "Select log directory",
            InitialDirectory = Directory.Exists(LogDirectory) ? LogDirectory : null,
        };
        if (dialog.ShowDialog() == true)
        {
            LogDirectory = dialog.FolderName;
        }
    }

    [RelayCommand]
    private void AddBaudRate()
    {
        if (NewBaudRate <= 0 || BaudRates.Contains(NewBaudRate))
        {
            return;
        }

        BaudRates.Add(NewBaudRate);
        List<int> ordered = [.. BaudRates.Order()];
        BaudRates.Clear();
        foreach (int value in ordered)
        {
            BaudRates.Add(value);
        }

        NewBaudRate = 0;
        MarkSettingsDirty();
    }

    [RelayCommand]
    private void RemoveBaudRate(int value)
    {
        bool isUsed = value == BaudRate || value == SerialParameterBaudRate ||
            Sessions.Any(session => session.BaudRate == value);
        if (BaudRates.Count > 1 && !isUsed)
        {
            BaudRates.Remove(value);
            MarkSettingsDirty();
        }
    }

    [RelayCommand]
    private static void OpenBackupFolder()
    {
        Directory.CreateDirectory(UserDataBackupService.BackupDirectory);
        Process.Start(new ProcessStartInfo(UserDataBackupService.BackupDirectory) { UseShellExecute = true });
    }

    private void ApplySystemBehaviorSettings()
    {
        SystemPowerService.SetPreventSleep(PreventSleep);
        if (!AutoBackupEnabled)
        {
            return;
        }

        DateTimeOffset? latest = UserDataBackupService.GetLatestBackupTime();
        if (latest.HasValue && DateTimeOffset.UtcNow - latest.Value < TimeSpan.FromDays(Math.Max(1, AutoBackupPeriodDays)))
        {
            return;
        }

        try
        {
            string path = UserDataBackupService.CreateBackup();
            Program.DiagnosticLog?.Information($"Automatic user-data backup created. Path={path}");
        }
        catch (Exception exception)
        {
            Program.DiagnosticLog?.Warning($"Automatic user-data backup failed. {exception.Message}");
        }
    }

    [RelayCommand]
    private void RestoreDefaultSettings()
    {
        if (!ThemedMessageDialog.Confirm(
            _settingsWindow ?? Application.Current.MainWindow,
            GetResourceString("Settings.RestoreDefaults.Confirmation"),
            GetResourceString("Settings.RestoreDefaults")))
        {
            return;
        }

        _isLoadingSettings = true;
        try
        {
            BaudRate = 1_152_000;
            SerialParameterBaudRate = 1_152_000;
            RestoreDefaultBaudRates();
            OnPropertyChanged(nameof(BaudRate));
            OnPropertyChanged(nameof(SerialParameterBaudRate));
            DataBits = 8;
            StopBits = StopBits.One;
            Parity = Parity.None;
            Handshake = Handshake.None;
            EncodingName = Encoding.UTF8.WebName;
            ReceiveMode = ReceiveDisplayMode.Str;
            TimestampEnabled = true;
            TimestampFormat = "HH:mm:ss.fff";
            LoggingEnabled = true;
            LogDirectory = GetDefaultLogDirectory();
            DefaultSendMode = SendMode.Str;
            LogRotationMegabytes = 40;
            LogRotationEnabled = true;
            DisplayBudgetMegabytes = 64;
            PrivateMemoryMonitorEnabled = false;
            PrivateMemoryThresholdMiB = 1024;
            LogFileNameFormat = "{Port}-{yyyy}-{MM}-{dd} {HH}-{mm}-{ss}.{fff}";
            FreezeAfterSend = false;
            SendPrefixEnabled = true;
            SendPrefix = "TX > ";
            ShowPortType = true;
            PauseFollowOnMouseWheel = true;
            PauseFollowOnFocus = false;
            ShowPauseHint = true;
            AutoBackupEnabled = true;
            AutoBackupPeriodDays = 7;
            PreventSleep = false;
            CloseToTaskbar = false;
            SearchOpacity = 1d;
            DefaultNewline = NewlinePolicy.None;
            IsSidebarVisible = true;
            ShowHiddenPorts = false;
            ShowSerialPorts = true;
            ShowVirtualPorts = true;
            BackgroundImageEnabled = false;
            BackgroundImagePath = string.Empty;
            BackgroundImageFolderPath = string.Empty;
            BackgroundImagePlaybackMode = BackgroundImagePlaybackMode.SingleImage;
            BackgroundImageIntervalSeconds = 300;
            BackgroundImageOpacity = 0.18d;
            PortSortMode = PortSortMode.NameAscending;
            WordWrap = false;
            ShowLineNumbers = false;
            HighlightCurrentLine = true;
            ShowControlCharacters = false;
            ShowSpaces = false;
            ShowTabs = false;
            LogFontSize = 14;
            LogFontFamily = "Cascadia Mono";
            TelnetPort = 23;
            TelnetAllowRemote = false;
        }
        finally
        {
            _isLoadingSettings = false;
        }

        SystemPowerService.SetPreventSleep(false);
        ApplyDefaultSettingsToEditor();
        SerialParameterSendMode = DefaultSendMode;
        SerialParameterNewline = DefaultNewline;
        PluginManager.SyncFromMainViewModel();
        _ = Application.Current.Dispatcher.BeginInvoke(
            () =>
            {
                OnPropertyChanged(nameof(SerialParameterBaudRate));
                foreach (SessionViewModel session in Sessions)
                {
                    session.RefreshBaudRateDisplay();
                }
            },
            System.Windows.Threading.DispatcherPriority.DataBind);
        SaveSettings();
    }

    [RelayCommand]
    private async Task RestoreCurrentPortDefaultsAsync()
    {
        SessionViewModel? session = _portSettingsTargetSession;
        if (session is null || session.IsBusy)
        {
            return;
        }

        _portSettingsApplyTimer.Stop();
        _portSettingsApplyPending = false;
        SerialPortSettings current = session.WorkspaceSession.Settings;
        SerialPortSettings defaults = current with
        {
            BaudRate = BaudRate,
            DataBits = DataBits,
            StopBits = StopBits,
            Parity = Parity,
            Handshake = Handshake,
            EncodingName = EncodingName,
            DtrEnable = false,
            RtsEnable = false,
            DiscardNull = false,
        };
        await ApplyPortSettingsAsync(session, defaults);
        if (session.WorkspaceSession.Settings != defaults)
        {
            return;
        }

        ApplySessionSettingsToEditor(defaults);

        _isLoadingSettings = true;
        try
        {
            SerialParameterReceiveMode = ReceiveMode;
            SerialParameterTimestampEnabled = TimestampEnabled;
            SerialParameterLoggingEnabled = LoggingEnabled;
            SerialParameterFollowEnd = true;
            SerialParameterFilterEnabled = true;
            SerialParameterSendMode = DefaultSendMode;
            SerialParameterNewline = DefaultNewline;
        }
        finally
        {
            _isLoadingSettings = false;
        }

        session.ReceiveMode = ReceiveMode;
        session.TimestampEnabled = TimestampEnabled;
        session.LoggingEnabled = LoggingEnabled;
        session.FollowEnd = true;
        session.FilterEnabled = true;
        session.SendMode = DefaultSendMode;
        session.Newline = DefaultNewline;
        RememberPortOverride(session.PortName);
    }

    internal void NotifyAutoScrollPaused()
    {
        if (ShowPauseHint)
        {
            StatusMessage = GetResourceString("Status.AutoScrollPaused");
        }
    }

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
        _backgroundImageTimer.Stop();
        _backgroundImageTimer.Tick -= OnBackgroundImageTimerTick;
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

    partial void OnSelectedSessionChanged(SessionViewModel? value)
    {
        _sendHistoryNavigator.Reset();
        NotifyCommandStates();
    }

    /// <summary>Activates the session whose log surface the user is interacting with.</summary>
    internal void ActivateLogSession(SessionViewModel session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _activeLogSession = session;
        if (RightSessions.Contains(session))
        {
            SelectedRightSession = session;
        }
        else if (Sessions.Contains(session))
        {
            SelectedSession = session;
        }
        else
        {
            return;
        }
    }

    private void ApplySessionSettingsToEditor(SerialPortSettings settings)
    {
        _isLoadingSettings = true;
        try
        {
            SerialParameterBaudRate = settings.BaudRate;
            SerialParameterDataBits = settings.DataBits;
            SerialParameterStopBits = settings.StopBits;
            SerialParameterParity = settings.Parity;
            SerialParameterHandshake = settings.Handshake;
            SerialParameterEncodingName = settings.EncodingName;
            SerialParameterDtrEnable = settings.DtrEnable;
            SerialParameterRtsEnable = settings.RtsEnable;
            SerialParameterDiscardNull = settings.DiscardNull;
        }
        finally
        {
            _isLoadingSettings = false;
        }

        PluginManager?.SyncFromMainViewModel();
    }

    partial void OnSelectedPortItemChanged(PortItemViewModel? value)
    {
        OnPropertyChanged(nameof(LogFileNamePreview));
        SessionViewModel? session = Sessions.FirstOrDefault(
            item => string.Equals(item.PortName, value?.PortName, StringComparison.OrdinalIgnoreCase));
        if (session is not null)
        {
            if (RightSessions.Contains(session))
            {
                return;
            }

            SelectedSession = session;
        }
        if (value is not null && session is null)
        {
            session = CreateClosedSession(value.PortName);
            Sessions.Add(session);
            SelectedSession = session;
        }
    }

    private SessionViewModel CreateClosedSession(string portName)
    {
        SerialPortSettings defaults = SerialPortSettings.Default(portName);
        _portOverrides.TryGetValue(portName, out PortSettingSnapshot? overrideValues);
        SerialPortSettings settings = defaults with
        {
            BaudRate = overrideValues?.BaudRate ?? BaudRate,
            DataBits = overrideValues?.DataBits ?? DataBits,
            StopBits = overrideValues?.StopBits ?? StopBits,
            Parity = overrideValues?.Parity ?? Parity,
            Handshake = overrideValues?.Handshake ?? Handshake,
            EncodingName = overrideValues?.EncodingName ?? EncodingName,
            DtrEnable = overrideValues?.DtrEnable ?? defaults.DtrEnable,
            RtsEnable = overrideValues?.RtsEnable ?? defaults.RtsEnable,
            DiscardNull = overrideValues?.DiscardNull ?? defaults.DiscardNull,
        };
        SessionViewModel session = CreateSession(settings, ResolvePortPreferences(portName));
        session.AutoReconnect = overrideValues?.AutoReconnect ?? false;
        return session;
    }

    private SessionViewModel CreateSession(SerialPortSettings settings, PortSessionPreferences preferences)
    {
        WorkspaceSessionOptions options = new(
            settings,
            preferences.ReceiveMode,
            preferences.TimestampEnabled,
            preferences.LoggingEnabled,
            preferences.LogDirectory,
            preferences.LogRotationBytes,
            preferences.LogRotationEnabled,
            preferences.DisplayBudgetBytes,
            preferences.LogFileNameFormat,
            preferences.SendPrefixEnabled,
            preferences.SendPrefix,
            preferences.TimestampFormat);
        return new SessionViewModel(
            _sessionFactory(options),
            preferences.ReceiveMode,
            preferences.TimestampEnabled,
            preferences.LoggingEnabled,
            preferences.SendMode,
            preferences.Newline,
            preferences.FollowEnd,
            preferences.FilterEnabled,
            HighlightRuleProjects,
            preferences.HighlightRuleProjectId);
    }

    private PortSessionPreferences ResolvePortPreferences(string portName)
    {
        _portOverrides.TryGetValue(portName, out PortSettingSnapshot? values);
        return new PortSessionPreferences(
            values?.ReceiveMode ?? ReceiveMode,
            values?.TimestampEnabled ?? TimestampEnabled,
            values?.LoggingEnabled ?? LoggingEnabled,
            LogDirectory,
            Math.Max(1, LogRotationMegabytes) * 1024L * 1024L,
            LogRotationEnabled,
            Math.Clamp(DisplayBudgetMegabytes, 16, 512) * 1024 * 1024,
            LogFileNameFormat,
            SendPrefixEnabled,
            SendPrefix,
            TimestampFormat,
            values?.FollowEnd ?? true,
            values?.FilterEnabled ?? true,
            values?.SendMode ?? DefaultSendMode,
            values?.Newline ?? DefaultNewline,
            values?.HighlightRuleProjectId ?? HighlightRuleProjects.FirstOrDefault()?.Id);
    }

    private async Task<SessionViewModel> RebuildClosedSessionAsync(SessionViewModel session)
    {
        int sessionIndex = Sessions.IndexOf(session);
        int rightIndex = RightSessions.IndexOf(session);
        bool selected = ReferenceEquals(SelectedSession, session);
        bool selectedRight = ReferenceEquals(SelectedRightSession, session);
        SerialPortSettings settings = session.WorkspaceSession.Settings;
        CloseFloatSendFor(session.PortName); CloseLogFilterFor(session.PortName);
        if (rightIndex >= 0)
        {
            RightSessions.RemoveAt(rightIndex);
        }

        Sessions.RemoveAt(sessionIndex);
        await session.DisposeAsync();
        SessionViewModel replacement = CreateSession(settings, ResolvePortPreferences(settings.PortName));
        replacement.AutoReconnect = session.AutoReconnect;
        Sessions.Insert(sessionIndex, replacement);
        if (rightIndex >= 0)
        {
            replacement.IsInRightPane = true;
            RightSessions.Insert(Math.Min(rightIndex, RightSessions.Count), replacement);
        }

        if (selected)
        {
            SelectedSession = replacement;
        }
        if (selectedRight)
        {
            SelectedRightSession = replacement;
        }

        return replacement;
    }

    private void RememberPortOverride(string portName)
    {
        SessionViewModel? session = Sessions.FirstOrDefault(item =>
            string.Equals(item.PortName, portName, StringComparison.OrdinalIgnoreCase));
        SerialPortSettings? settings = session?.WorkspaceSession.Settings;
        _portOverrides.TryGetValue(portName, out PortSettingSnapshot? previous);
        _portOverrides[portName] = new PortSettingSnapshot(
            settings?.BaudRate ?? previous?.BaudRate ?? BaudRate,
            settings?.DataBits ?? previous?.DataBits ?? DataBits,
            settings?.StopBits ?? previous?.StopBits ?? StopBits,
            settings?.Parity ?? previous?.Parity ?? Parity,
            settings?.Handshake ?? previous?.Handshake ?? Handshake,
            settings?.EncodingName ?? previous?.EncodingName ?? EncodingName,
            settings?.DtrEnable ?? previous?.DtrEnable,
            settings?.RtsEnable ?? previous?.RtsEnable,
            settings?.DiscardNull ?? previous?.DiscardNull,
            session?.SendMode ?? previous?.SendMode,
            session?.Newline ?? previous?.Newline,
            session?.ReceiveMode ?? previous?.ReceiveMode,
            session?.TimestampEnabled ?? previous?.TimestampEnabled,
            session?.LoggingEnabled ?? previous?.LoggingEnabled,
            LogDirectory,
            LogRotationMegabytes,
            LogRotationEnabled,
            DisplayBudgetMegabytes,
            LogFileNameFormat,
            SendPrefixEnabled,
            SendPrefix,
            session?.FollowEnd ?? previous?.FollowEnd,
            session?.FilterEnabled ?? previous?.FilterEnabled,
            AutoReconnect: session?.AutoReconnect ?? previous?.AutoReconnect);
        MarkSettingsDirty();
    }

    internal void RememberSessionHighlightProject(SessionViewModel session)
    {
        ArgumentNullException.ThrowIfNull(session);
        RememberPortOverride(session.PortName);
    }

    internal void ApplySessionHighlightProject(SessionViewModel session, Guid? projectId)
    {
        ArgumentNullException.ThrowIfNull(session);
        session.ApplyHighlightRuleProject(projectId);
        RememberSessionHighlightProject(session);
    }

    private void OnHighlightRulesApplied(object? sender, HighlightRulesAppliedEventArgs e)
    {
        SessionViewModel? session = _activeLogSession ?? SelectedSession ?? SelectedRightSession;
        if (session is null)
        {
            StatusMessage = GetResourceString("Status.NoSessionSelected");
            return;
        }

        session.ReplaceHighlightRuleProjects(e.Projects);
        session.ApplyHighlightRuleProject(e.SelectedProjectId);
        RememberSessionHighlightProject(session);
        StatusMessage = GetResourceString("HighlightFilter.ApplySuccess");
    }

    private void OnHighlightRuleProjectsChanged(object? sender, HighlightRuleProjectsChangedEventArgs e)
    {
        HighlightRuleProjects.Clear();
        foreach (HighlightFilterRuleProject project in e.Projects)
        {
            HighlightRuleProjects.Add(project);
        }

        HighlightFilterRules.Clear();
        foreach (HighlightFilterRule rule in e.Projects.SelectMany(project => project.Rules))
        {
            HighlightFilterRules.Add(rule);
        }

        foreach (SessionViewModel session in Sessions)
        {
            Guid? previousProjectId = session.HighlightRuleProjectId;
            session.ReplaceHighlightRuleProjects(e.Projects);
            if (previousProjectId != session.HighlightRuleProjectId)
            {
                RememberSessionHighlightProject(session);
            }
        }
    }

    private void RememberPortOverride(string portName, SerialPortSettings settings)
    {
        _portOverrides[portName] = new PortSettingSnapshot(
            settings.BaudRate,
            settings.DataBits,
            settings.StopBits,
            settings.Parity,
            settings.Handshake,
            settings.EncodingName,
            settings.DtrEnable,
            settings.RtsEnable,
            settings.DiscardNull,
            Sessions.FirstOrDefault(item => string.Equals(item.PortName, portName, StringComparison.OrdinalIgnoreCase))?.SendMode,
            Sessions.FirstOrDefault(item => string.Equals(item.PortName, portName, StringComparison.OrdinalIgnoreCase))?.Newline,
            Sessions.FirstOrDefault(item => string.Equals(item.PortName, portName, StringComparison.OrdinalIgnoreCase))?.ReceiveMode,
            Sessions.FirstOrDefault(item => string.Equals(item.PortName, portName, StringComparison.OrdinalIgnoreCase))?.TimestampEnabled,
            Sessions.FirstOrDefault(item => string.Equals(item.PortName, portName, StringComparison.OrdinalIgnoreCase))?.LoggingEnabled,
            LogDirectory,
            LogRotationMegabytes,
            LogRotationEnabled,
            DisplayBudgetMegabytes,
            LogFileNameFormat,
            SendPrefixEnabled,
            SendPrefix,
            FollowEnd: Sessions.FirstOrDefault(item => string.Equals(item.PortName, portName, StringComparison.OrdinalIgnoreCase))?.FollowEnd,
            FilterEnabled: Sessions.FirstOrDefault(item => string.Equals(item.PortName, portName, StringComparison.OrdinalIgnoreCase))?.FilterEnabled,
            AutoReconnect: Sessions.FirstOrDefault(item => string.Equals(item.PortName, portName, StringComparison.OrdinalIgnoreCase))?.AutoReconnect);
        MarkSettingsDirty();
    }

    private void ApplyDefaultSettingsToEditor()
    {
        _isLoadingSettings = true;
        try
        {
            SerialParameterBaudRate = BaudRate;
            SerialParameterDataBits = DataBits;
            SerialParameterStopBits = StopBits;
            SerialParameterParity = Parity;
            SerialParameterHandshake = Handshake;
            SerialParameterEncodingName = EncodingName;
            SerialParameterDtrEnable = false;
            SerialParameterRtsEnable = false;
            SerialParameterDiscardNull = false;
            SerialParameterAutoReconnect = false;
        }
        finally
        {
            _isLoadingSettings = false;
        }
    }

    [RelayCommand]
    private void OpenLogFile()
    {
        OpenFileDialog dialog = new()
        {
            Filter = "Text logs (*.txt)|*.txt|All files (*.*)|*.*",
            InitialDirectory = Directory.Exists(LogDirectory) ? LogDirectory : null,
        };
        if (dialog.ShowDialog() == true)
        {
            Process.Start(new ProcessStartInfo(dialog.FileName) { UseShellExecute = true });
        }
    }

    [RelayCommand]
    private async Task LoadSendFileAsync(SessionViewModel? session)
    {
        session ??= _portSettingsTargetSession ?? SelectedSession ?? SelectedRightSession;
        if (session is null)
        {
            return;
        }

        OpenFileDialog dialog = new() { Filter = GetResourceString("Send.FileFilter"), CheckFileExists = true };
        if (dialog.ShowDialog() == true)
        {
            session.SendText = await File.ReadAllTextAsync(dialog.FileName, Encoding.UTF8);
        }
    }

    [RelayCommand]
    private async Task SaveVisibleLogAsync()
    {
        SessionViewModel? session = SelectedSession;
        if (session is null)
        {
            return;
        }

        SaveFileDialog dialog = new()
        {
            Filter = "Text logs (*.txt)|*.txt",
            FileName = $"{session.PortName}-visible.txt",
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        string[] lines = [.. session.VisibleLines.Select(line => line.Text)];
        await Task.Run(() => File.WriteAllLines(dialog.FileName, lines, new UTF8Encoding(false)));
    }

    [RelayCommand]
    private async Task SaveVisibleLogAsHexAsync()
    {
        SessionViewModel? session = SelectedSession;
        if (session is null)
        {
            return;
        }

        SaveFileDialog dialog = new()
        {
            Filter = "HEX text (*.txt)|*.txt",
            FileName = $"{session.PortName}-visible.hex.txt",
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        string[] lines = [.. session.VisibleLines.Select(line => line.Text)];
        Encoding encoding = GetSessionEncoding(session);
        await Task.Run(() =>
        {
            string[] hexLines = [.. lines.Select(line => DuCom.Core.Sending.HexRepresentation.ToHexText(encoding.GetBytes(line)))];
            File.WriteAllLines(dialog.FileName, hexLines, new UTF8Encoding(false));
        });
    }

    [RelayCommand]
    private async Task SaveVisibleLogAsBinaryAsync()
    {
        SessionViewModel? session = SelectedSession;
        if (session is null)
        {
            return;
        }

        SaveFileDialog dialog = new()
        {
            Filter = "Binary (*.bin)|*.bin",
            FileName = $"{session.PortName}-visible.bin",
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        string[] lines = [.. session.VisibleLines.Select(line => line.Text)];
        Encoding encoding = GetSessionEncoding(session);
        await Task.Run(() =>
        {
            using FileStream stream = new(dialog.FileName, FileMode.Create, FileAccess.Write, FileShare.None);
            foreach (string line in lines)
            {
                byte[] payload = encoding.GetBytes(line);
                stream.Write(payload, 0, payload.Length);
                stream.WriteByte((byte)'\n');
            }
        });
    }

    private static Encoding GetSessionEncoding(SessionViewModel session)
    {
        try
        {
            return Encoding.GetEncoding(session.WorkspaceSession.Settings.EncodingName);
        }
        catch (ArgumentException)
        {
            return Encoding.UTF8;
        }
    }

    [RelayCommand]
    private void ClipboardToHex()
    {
        string text = Clipboard.GetText();
        if (string.IsNullOrEmpty(text))
        {
            StatusMessage = GetResourceString("Status.ClipboardEmpty");
            return;
        }

        Clipboard.SetText(DuCom.Core.Sending.HexRepresentation.ToHexText(Encoding.UTF8.GetBytes(text)));
        StatusMessage = string.Empty;
    }

    [RelayCommand]
    private void ClipboardFromHex()
    {
        string text = Clipboard.GetText();
        if (string.IsNullOrEmpty(text))
        {
            StatusMessage = GetResourceString("Status.ClipboardEmpty");
            return;
        }

        if (!DuCom.Core.Sending.HexRepresentation.TryParseHexText(text, out byte[] bytes))
        {
            StatusMessage = GetResourceString("Status.InvalidHex").Replace("{0}", text.Trim(), StringComparison.Ordinal);
            return;
        }

        Clipboard.SetText(Encoding.UTF8.GetString(bytes));
        StatusMessage = string.Empty;
    }

    [RelayCommand]
    private void ClipboardTimestampsToLocal()
    {
        string text = Clipboard.GetText();
        if (string.IsNullOrEmpty(text))
        {
            StatusMessage = GetResourceString("Status.ClipboardEmpty");
            return;
        }

        Clipboard.SetText(DuCom.Core.Parsing.DisplayTextTransform.TimestampsToLocal(text));
        StatusMessage = string.Empty;
    }

    [RelayCommand]
    private void ExportConfiguration()
    {
        SaveFileDialog dialog = new() { Filter = "DuCom settings (*.json)|*.json", FileName = "ducom-settings.json" };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        ConfigurationSnapshot snapshot = CaptureConfiguration();
        File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(snapshot, ConfigurationJsonOptions));
    }

    [RelayCommand]
    private void ImportConfiguration()
    {
        OpenFileDialog dialog = new() { Filter = "DuCom settings (*.json)|*.json" };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        ConfigurationSnapshot? snapshot = JsonSerializer.Deserialize<ConfigurationSnapshot>(File.ReadAllText(dialog.FileName), ConfigurationJsonOptions);
        if (snapshot is not null)
        {
            ApplyConfiguration(snapshot);
        }
    }

    [RelayCommand]
    private static void OpenApplicationFolder() =>
        Process.Start(new ProcessStartInfo(AppContext.BaseDirectory) { UseShellExecute = true });

    [RelayCommand]
    private static void ExitApplication()
    {
        if (Application.Current.MainWindow is MainWindow window)
        {
            window.RequestExit();
        }
    }

    [RelayCommand]
    private void CopyVisibleLog()
    {
        if (SelectedSession is not null)
        {
            Clipboard.SetText(string.Join(Environment.NewLine, SelectedSession.VisibleLines.Select(line => line.Text)));
        }
    }

    [RelayCommand]
    private static void OpenDiagnosticFolder()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Logs", "System_log");
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    [RelayCommand]
    private static void OpenDocumentation()
    {
        string path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "docs"));
        if (Directory.Exists(path))
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
    }

    [RelayCommand]
    private static void ShowAbout()
    {
        AboutWindow window = new() { Owner = Application.Current.MainWindow };
        window.ShowDialog();
    }

    [RelayCommand]
    private static void OpenFeedback() =>
        Process.Start(new ProcessStartInfo("mailto:du?subject=DuCom%20Feedback") { UseShellExecute = true });

    [RelayCommand]
    private void ApplyLightTheme()
    {
        ((App)Application.Current).ApplyTheme("Light");
        SyncAppearanceSelection();
        MarkSettingsDirty();
    }

    [RelayCommand]
    private void ApplyDarkTheme()
    {
        ((App)Application.Current).ApplyTheme("Dark");
        SyncAppearanceSelection();
        MarkSettingsDirty();
    }

    [RelayCommand]
    private void ApplyChineseLanguage()
    {
        ((App)Application.Current).ApplyLanguage("zh-CN");
        SyncAppearanceSelection();
    }

    [RelayCommand]
    private void ApplyEnglishLanguage()
    {
        ((App)Application.Current).ApplyLanguage("en-US");
        SyncAppearanceSelection();
    }

    private void SyncAppearanceSelection()
    {
        App app = (App)Application.Current;
        IsChineseLanguage = app.CurrentLanguage == "zh-CN";
        IsEnglishLanguage = app.CurrentLanguage == "en-US";
        IsSystemTheme = false;
        IsLightTheme = app.CurrentThemeMode == "Light";
        IsDarkTheme = app.CurrentThemeMode == "Dark";
    }

    [RelayCommand]
    private static void ApplyMicaSkin() => ApplyBackdrop(WindowBackdropType.Mica);

    [RelayCommand]
    private static void ApplyAcrylicSkin() => ApplyBackdrop(WindowBackdropType.Acrylic);

    [RelayCommand]
    private static void ApplySolidSkin() => ApplyBackdrop(WindowBackdropType.None);

    [RelayCommand]
    private void ToggleSidebar() => IsSidebarVisible = !IsSidebarVisible;

    [RelayCommand]
    private void ToggleFollowEnd()
    {
        SessionViewModel? session = SelectedSession ?? SelectedRightSession;
        if (session is not null)
        {
            session.FollowEnd = !session.FollowEnd;
        }
    }

    [RelayCommand]
    private void ToggleDefaultReceiveMode()
    {
        if (SelectedSession is not null)
        {
            SelectedSession.ReceiveMode = SelectedSession.ReceiveMode == ReceiveDisplayMode.Str ? ReceiveDisplayMode.Hex : ReceiveDisplayMode.Str;
            RememberPortOverride(SelectedSession.PortName);
        }
        else
        {
            ReceiveMode = ReceiveMode == ReceiveDisplayMode.Str ? ReceiveDisplayMode.Hex : ReceiveDisplayMode.Str;
        }
        StatusMessage = GetResourceString("Status.SessionSettingRequiresReopen");
    }

    [RelayCommand]
    private void ToggleDefaultTimestamp()
    {
        if (SelectedSession is not null)
        {
            SelectedSession.TimestampEnabled = !SelectedSession.TimestampEnabled;
            RememberPortOverride(SelectedSession.PortName);
        }
        else
        {
            TimestampEnabled = !TimestampEnabled;
        }
        StatusMessage = GetResourceString("Status.SessionSettingRequiresReopen");
    }

    [RelayCommand]
    private void ToggleSelectedSendMode()
    {
        if (SelectedSession is not null)
        {
            SelectedSession.SendMode = SelectedSession.SendMode == SendMode.Str ? SendMode.Hex : SendMode.Str;
            RememberPortOverride(SelectedSession.PortName);
        }
    }

    internal Task AssignRightPaneAsync(string portName)
    {
        SessionViewModel? primarySession = SelectedSession;
        SessionViewModel? session = Sessions.FirstOrDefault(item =>
            string.Equals(item.PortName, portName, StringComparison.OrdinalIgnoreCase));
        if (session is null)
        {
            session = CreateClosedSession(portName);
            Sessions.Add(session);
        }

        SessionViewModel? leftSession = primarySession is not null && !ReferenceEquals(primarySession, session)
            && !RightSessions.Contains(primarySession)
            ? primarySession
            : Sessions.FirstOrDefault(item => item.IsOpen && !ReferenceEquals(item, session) && !RightSessions.Contains(item))
                ?? Sessions.FirstOrDefault(item => !ReferenceEquals(item, session) && !RightSessions.Contains(item));
        if (leftSession is null)
        {
            StatusMessage = GetResourceString("Status.SplitNeedsAnotherSession");
            return Task.CompletedTask;
        }

        session.IsInRightPane = true;
        if (!RightSessions.Contains(session))
        {
            RightSessions.Add(session);
        }
        SelectedRightSession = session;
        SelectedSession = leftSession;
        SelectedPortItem = AvailablePorts.FirstOrDefault(item =>
            string.Equals(item.PortName, leftSession.PortName, StringComparison.OrdinalIgnoreCase));
        StatusMessage = string.Empty;
        return Task.CompletedTask;
    }

    [RelayCommand]
    private async Task CloseRightPaneAsync(SessionViewModel? session)
    {
        session ??= SelectedRightSession;
        if (session is null)
        {
            return;
        }

        RememberPortOverride(session.PortName);
        CloseFloatSendFor(session.PortName); CloseLogFilterFor(session.PortName);
        RemoveRightSession(session);
        Sessions.Remove(session);
        await session.DisposeAsync();
        NotifyCommandStates();
    }

    [RelayCommand]
    private async Task CloseSessionAsync(SessionViewModel? session)
    {
        session ??= SelectedSession;
        if (session is null)
        {
            return;
        }

        if (session.IsOpen)
        {
            await session.CloseAsync();
        }

        CloseFloatSendFor(session.PortName);
        CloseLogFilterFor(session.PortName);
        if (RightSessions.Contains(session))
        {
            RemoveRightSession(session);
        }

        Sessions.Remove(session);
        await session.DisposeAsync();
        if (ReferenceEquals(SelectedSession, session))
        {
            SelectedSession = Sessions.FirstOrDefault(candidate => !RightSessions.Contains(candidate));
        }
        NotifyCommandStates();
    }

    [RelayCommand]
    private void MoveRightSessionToMain(SessionViewModel? session)
    {
        session ??= SelectedRightSession;
        if (session is null)
        {
            return;
        }

        RemoveRightSession(session);
        SelectedSession = session;
        SelectedPortItem = AvailablePorts.FirstOrDefault(item =>
            string.Equals(item.PortName, session.PortName, StringComparison.OrdinalIgnoreCase));
        NotifyCommandStates();
    }

    [RelayCommand]
    private void SetSplitOrientation(string orientation)
    {
        if (Enum.TryParse(orientation, true, out SplitLayoutOrientation parsed))
        {
            SplitOrientation = parsed;
        }
    }

    internal void MoveSessionTab(string portName, int targetIndex, bool rightPane)
    {
        ObservableCollection<SessionViewModel> collection = rightPane ? RightSessions : Sessions;
        SessionViewModel? session = collection.FirstOrDefault(item =>
            string.Equals(item.PortName, portName, StringComparison.OrdinalIgnoreCase));
        if (session is null)
        {
            return;
        }

        int currentIndex = collection.IndexOf(session);
        int boundedTarget = Math.Clamp(targetIndex, 0, collection.Count - 1);
        if (currentIndex != boundedTarget)
        {
            collection.Move(currentIndex, boundedTarget);
            MarkSettingsDirty();
        }
    }

    [RelayCommand]
    private async Task ToggleSessionConnectionAsync(SessionViewModel session)
    {
        ArgumentNullException.ThrowIfNull(session);
        try
        {
            if (session.IsOpen)
            {
                await session.CloseAsync();
            }
            else
            {
                await session.OpenAsync();
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or TimeoutException or OperationCanceledException)
        {
            StatusMessage = GetResourceString("Status.PortCommandFailed")
                .Replace("{0}", exception.Message, StringComparison.Ordinal);
            Program.DiagnosticLog?.Warning($"Port command failed. Port={session.PortName}; {exception.Message}");
        }

        NotifyCommandStates();
    }

    [RelayCommand]
    private async Task SendSessionAsync(SessionViewModel session)
    {
        ArgumentNullException.ThrowIfNull(session);
        try
        {
            await session.SendAsync();
            if (FreezeAfterSend)
            {
                session.FollowEnd = false;
            }

            if (_sendHistory.Record(session.SendText))
            {
                PersistSendHistory();
            }
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException)
        {
            StatusMessage = GetResourceString("Status.InvalidHex").Replace("{0}", exception.Message, StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or TimeoutException or OperationCanceledException)
        {
            StatusMessage = GetResourceString("Status.SendFailed").Replace("{0}", exception.Message, StringComparison.Ordinal);
            Program.DiagnosticLog?.Warning($"Send failed. Port={session.PortName}; {exception.Message}");
        }
    }

    [RelayCommand]
    private void ShowToolCenter(string page) =>
        new ToolCenterWindow(page, ShortcutManager, CommandRunner, this, Telnet) { Owner = Application.Current.MainWindow }.Show();

    internal IReadOnlyList<string> SendHistoryEntries => _sendHistory.Entries;

    internal IReadOnlyList<string> SearchSendHistoryEntries(string? query) => _sendHistory.Search(query);

    internal void UseSendHistoryEntry(string entry)
    {
        if (SelectedSession is not null)
        {
            SelectedSession.SendText = entry;
        }
    }

    internal void DeleteSendHistoryEntry(string entry)
    {
        List<string> remaining = [.. _sendHistory.Entries.Where(item => item != entry)];
        _sendHistory.Replace(remaining);
        PersistSendHistory();
    }

    internal void ClearSendHistory()
    {
        _sendHistory.Clear();
        PersistSendHistory();
    }

    private void PersistSendHistory()
    {
        try
        {
            SendHistoryFileService.Save(_sendHistory);
        }
        catch (Exception exception)
        {
            Program.DiagnosticLog?.Warning($"Failed to save send history. {exception.Message}");
        }
    }

    [RelayCommand]
    private void ShowHighlightFilterRules()
    {
        var window = new HighlightFilterRulesWindow(new HighlightFilterRuleService(HighlightFilterRulesFilePath))
        {
            Owner = Application.Current.MainWindow,
        };
        window.Closed += (_, _) => LoadHighlightFilterRules();
        window.Show();
    }

    internal void OpenSettingsCategory(int category)
    {
        _ = ToggleSettingsAsync();
        _ = Application.Current.Dispatcher.BeginInvoke(
            () => _settingsWindow?.SelectCategory(category),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    [RelayCommand]
    private void ShowPluginManager() => ShowToolCenter(ToolCenterPages.Plugins);

    private static void ApplyBackdrop(WindowBackdropType backdrop)
    {
        ApplicationTheme theme = ApplicationThemeManager.GetAppTheme();
        if (theme == ApplicationTheme.Unknown)
        {
            theme = ApplicationTheme.Dark;
        }

        ApplicationThemeManager.Apply(theme, backdrop, true);
        if (Application.Current.MainWindow is FluentWindow window)
        {
            window.WindowBackdropType = backdrop;
        }
    }

    private ConfigurationSnapshot CaptureConfiguration() => new(
        BaudRate,
        DataBits,
        StopBits,
        Parity,
        Handshake,
        EncodingName,
        ReceiveMode,
        TimestampEnabled,
        LoggingEnabled,
        LogDirectory,
        DefaultSendMode,
        DefaultNewline,
        TimestampFormat,
        LogRotationMegabytes,
        LogRotationEnabled,
        DisplayBudgetMegabytes,
        PrivateMemoryMonitorEnabled,
        Math.Clamp(PrivateMemoryThresholdMiB, 1, 1_048_576),
        LogFileNameFormat,
        FreezeAfterSend,
        SendPrefixEnabled,
        SendPrefix,
        PauseFollowOnMouseWheel,
        PauseFollowOnFocus,
        ShowPauseHint,
        AutoBackupEnabled,
        AutoBackupPeriodDays,
        PreventSleep,
        CloseToTaskbar,
        WordWrap,
        ShowLineNumbers,
        HighlightCurrentLine,
        ShowControlCharacters,
        ShowSpaces,
        ShowTabs,
        LogFontSize,
        LogFontFamily,
        ((App)Application.Current).CurrentLanguage,
        ((App)Application.Current).CurrentThemeMode == "Light" ? "Light" : "Dark",
        ShowPortType,
        SearchOpacity,
        IsSidebarVisible,
        PortSortMode,
        ShowHiddenPorts,
        ShowSerialPorts,
        ShowVirtualPorts,
        BackgroundImageEnabled,
        BackgroundImagePath,
        BackgroundImageFolderPath,
        BackgroundImagePlaybackMode,
        Math.Clamp(BackgroundImageIntervalSeconds, 1, 86_400),
        Math.Clamp(BackgroundImageOpacity, 0d, 1d),
        TelnetPort,
        TelnetAllowRemote,
        TelnetAuthenticationEnabled,
        TelnetUsername,
        [.. BaudRates],
        CapturePortOverrides(),
        [.. DuCom.Core.Persistence.PortVisibility.NormalizeHidden(_hiddenPorts)],
        [.. CommandTargetPortNames],
        [.. RightSessions.Where(session => session.IsOpen).Select(session => session.PortName)],
        SplitOrientation,
        Math.Clamp(SplitterRatio, 0.2d, 0.8d),
        [.. Sessions.Where(session => session.IsOpen).Select(session => session.PortName)],
        [.. Sessions.Where(session => session.IsOpen).Select(session => session.PortName)],
        SelectedSession is { IsOpen: true, IsInRightPane: false } ? SelectedSession.PortName : null,
        SelectedRightSession is { IsOpen: true, IsInRightPane: true } ? SelectedRightSession.PortName : null);

    private void ApplyConfiguration(ConfigurationSnapshot snapshot)
    {
        _isLoadingSettings = true;
        try
        {
            // Refill the baud-rate list before selecting the value: clearing the list after
            // the selection leaves the ComboBox empty (same ordering rule as restore-defaults).
            if (snapshot.CustomBaudRates is { Count: > 0 })
            {
                BaudRates.Clear();
                foreach (int value in snapshot.CustomBaudRates.Where(value => value > 0).Distinct().Order())
                {
                    BaudRates.Add(value);
                }
            }

            BaudRate = snapshot.BaudRate;
            SerialParameterBaudRate = snapshot.BaudRate;
            EnsureBaudRatePresent(snapshot.BaudRate);
            DataBits = snapshot.DataBits;
            StopBits = snapshot.StopBits;
            Parity = snapshot.Parity;
            Handshake = snapshot.Handshake;
            EncodingName = snapshot.EncodingName;
            ReceiveMode = snapshot.ReceiveMode;
            TimestampEnabled = snapshot.TimestampEnabled;
            TimestampFormat = TimestampFormatOptions.Contains(snapshot.TimestampFormat)
                ? snapshot.TimestampFormat
                : "HH:mm:ss.fff";
            LoggingEnabled = snapshot.LoggingEnabled;
            LogDirectory = snapshot.LogDirectory;
            DefaultSendMode = snapshot.SendMode;
            DefaultNewline = snapshot.Newline;
            LogRotationMegabytes = snapshot.LogRotationMegabytes;
            LogRotationEnabled = snapshot.LogRotationEnabled;
            DisplayBudgetMegabytes = snapshot.DisplayBudgetMegabytes;
            PrivateMemoryMonitorEnabled = snapshot.PrivateMemoryMonitorEnabled;
            PrivateMemoryThresholdMiB = Math.Clamp(snapshot.PrivateMemoryThresholdMiB, 1, 1_048_576);
            LogFileNameFormat = snapshot.LogFileNameFormat;
            FreezeAfterSend = snapshot.FreezeAfterSend;
            SendPrefixEnabled = snapshot.SendPrefixEnabled;
            SendPrefix = snapshot.SendPrefix;
            PauseFollowOnMouseWheel = snapshot.PauseFollowOnMouseWheel;
            PauseFollowOnFocus = snapshot.PauseFollowOnFocus;
            ShowPauseHint = snapshot.ShowPauseHint;
            AutoBackupEnabled = snapshot.AutoBackupEnabled;
            AutoBackupPeriodDays = snapshot.AutoBackupPeriodDays;
            PreventSleep = snapshot.PreventSleep;
            CloseToTaskbar = snapshot.CloseToTaskbar;
            WordWrap = snapshot.WordWrap;
            ShowLineNumbers = snapshot.ShowLineNumbers;
            HighlightCurrentLine = snapshot.HighlightCurrentLine;
            ShowControlCharacters = snapshot.ShowControlCharacters;
            ShowSpaces = snapshot.ShowSpaces;
            ShowTabs = snapshot.ShowTabs;
            LogFontSize = snapshot.LogFontSize == 12 ? 14 : snapshot.LogFontSize;
            LogFontFamily = LogFontFamilies.Contains(snapshot.LogFontFamily, StringComparer.OrdinalIgnoreCase)
                ? snapshot.LogFontFamily
                : "Cascadia Mono";
            ShowPortType = snapshot.ShowPortType;
            SearchOpacity = Math.Clamp(snapshot.SearchOpacity, 0.2d, 1d);
            IsSidebarVisible = snapshot.IsSidebarVisible;
            PortSortMode = snapshot.PortSortMode;
            ShowHiddenPorts = snapshot.ShowHiddenPorts;
            ShowSerialPorts = snapshot.ShowSerialPorts;
            ShowVirtualPorts = snapshot.ShowVirtualPorts;
            BackgroundImageEnabled = snapshot.BackgroundImageEnabled;
            BackgroundImagePath = snapshot.BackgroundImagePath ?? string.Empty;
            BackgroundImageFolderPath = snapshot.BackgroundImageFolderPath ?? string.Empty;
            BackgroundImagePlaybackMode = snapshot.BackgroundImagePlaybackMode;
            BackgroundImageIntervalSeconds = snapshot.BackgroundImageIntervalSeconds == 30
                ? 300
                : Math.Clamp(snapshot.BackgroundImageIntervalSeconds, 1, 86_400);
            BackgroundImageOpacity = Math.Clamp(snapshot.BackgroundImageOpacity, 0d, 1d);
            RefreshBackgroundImagePlayback();
            TelnetPort = Math.Clamp(snapshot.TelnetPort, 1, 65_535);
            TelnetAllowRemote = snapshot.TelnetAllowRemote;
            TelnetAuthenticationEnabled = snapshot.TelnetAuthenticationEnabled;
            TelnetUsername = snapshot.TelnetUsername ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(snapshot.Language))
            {
                ((App)Application.Current).ApplyLanguage(snapshot.Language);
            }

            if (!((App)Application.Current).IsThemeSpecifiedOnCommandLine)
            {
                ((App)Application.Current).ApplyTheme(snapshot.ThemeMode);
            }

            SystemPowerService.SetPreventSleep(PreventSleep);
            _portOverrides = new Dictionary<string, PortSettingSnapshot>(
                snapshot.PortOverrides ?? [],
                StringComparer.OrdinalIgnoreCase);
            EnsureActiveBaudRatesPresent();
            _persistedRightPanePorts = [.. snapshot.RightPanePorts ?? []];
            _persistedSessionOrder = [.. snapshot.SessionOrder ?? []];
            _persistedOpenSessionPorts = [.. snapshot.OpenSessionPorts ?? []];
            _persistedSelectedSessionPort = snapshot.SelectedSessionPort;
            _persistedSelectedRightSessionPort = snapshot.SelectedRightSessionPort;
            SplitOrientation = snapshot.SplitOrientation;
            SplitterRatio = Math.Clamp(snapshot.SplitterRatio, 0.2d, 0.8d);
            _hiddenPorts.Clear();
            foreach (string hidden in DuCom.Core.Persistence.PortVisibility.NormalizeHidden(snapshot.HiddenPorts))
            {
                _hiddenPorts.Add(hidden);
            }
            Volatile.Write(ref _commandTargetPortNames, NormalizePortNames(snapshot.CommandTargetPortNames));
        }
        finally
        {
            _isLoadingSettings = false;
        }
    }

    private Dictionary<string, PortSettingSnapshot> CapturePortOverrides()
    {
        Dictionary<string, PortSettingSnapshot> result = new(_portOverrides, StringComparer.OrdinalIgnoreCase);
        foreach (SessionViewModel session in Sessions)
        {
            SerialPortSettings settings = session.WorkspaceSession.Settings;
            result[session.PortName] = new PortSettingSnapshot(
                settings.BaudRate,
                settings.DataBits,
                settings.StopBits,
                settings.Parity,
                settings.Handshake,
                settings.EncodingName,
                settings.DtrEnable,
                settings.RtsEnable,
                settings.DiscardNull,
                session.SendMode,
                session.Newline,
                session.ReceiveMode,
                session.TimestampEnabled,
                session.LoggingEnabled,
                LogDirectory,
                LogRotationMegabytes,
                LogRotationEnabled,
                DisplayBudgetMegabytes,
                LogFileNameFormat,
                SendPrefixEnabled,
                SendPrefix,
                FollowEnd: session.FollowEnd,
                FilterEnabled: session.FilterEnabled,
                AutoReconnect: session.AutoReconnect,
                HighlightRuleProjectId: session.HighlightRuleProjectId);
        }

        return result;
    }

    private void LoadSettings()
    {
        ConfigurationSnapshot? snapshot = AppSettingsService.Load<ConfigurationSnapshot>();
        if (snapshot is not null)
        {
            ApplyConfiguration(snapshot);
            Program.DiagnosticLog?.Information($"Loaded settings from {AppSettingsService.SettingsFilePath}.");
        }
    }

    private void LoadHighlightFilterRules()
    {
        try
        {
            var service = new HighlightFilterRuleService(HighlightFilterRulesFilePath);
            List<HighlightFilterRuleProject> projects = [.. service.LoadProjects()];
            if (projects.Count == 0)
            {
                projects.Add(new HighlightFilterRuleProject(Guid.NewGuid(), "default", DefaultDuComData.MergeHighlightRules([], out _)));
                service.SaveProjects(projects);
            }
            else if (projects.Count == 1 &&
                     projects[0].Name is "BES Default" or "Imported rules" or "Rules")
            {
                projects[0] = projects[0] with { Name = "default" };
                service.SaveProjects(projects);
            }
            else
            {
                bool repaired = false;
                for (int index = 0; index < projects.Count; index++)
                {
                    if (!string.Equals(projects[index].Name, "default", StringComparison.OrdinalIgnoreCase) ||
                        projects[index].Rules.Count > 0)
                    {
                        continue;
                    }

                    projects[index] = projects[index] with
                    {
                        Name = "default",
                        Rules = DefaultDuComData.MergeHighlightRules([], out _),
                    };
                    repaired = true;
                }

                if (repaired)
                {
                    service.SaveProjects(projects);
                    Program.DiagnosticLog?.Information("Repaired empty default highlight-rule project.");
                }
            }

            HighlightRuleProjects.Clear();
            foreach (HighlightFilterRuleProject project in projects)
            {
                HighlightRuleProjects.Add(project);
            }
            HighlightFilterRules.Clear();
            foreach (HighlightFilterRule rule in projects.SelectMany(project => project.Rules))
            {
                HighlightFilterRules.Add(rule);
            }

            foreach (SessionViewModel session in Sessions)
            {
                session.ReplaceHighlightRuleProjects(HighlightRuleProjects);
            }

            Program.DiagnosticLog?.Information($"Loaded {HighlightRuleProjects.Count} highlight rule projects and {HighlightFilterRules.Count} rules from {HighlightFilterRulesFilePath}.");
        }
        catch (Exception exception)
        {
            Program.DiagnosticLog?.Warning($"Failed to load highlight/filter rules. {exception.Message}");
        }
    }

    private void LoadWatchdogRules()
    {
        WatchdogRules.Clear();
        foreach (WatchdogRule rule in Services.WatchdogRuleStore.Load())
        {
            WatchdogRules.Add(rule);
        }

        Watchdog.UpdateRules([.. WatchdogRules]);
        Program.DiagnosticLog?.Information($"Loaded {WatchdogRules.Count} watchdog rules.");
    }

    internal void SaveWatchdogRules(WatchdogRule[] rules)
    {
        WatchdogRules.Clear();
        foreach (WatchdogRule rule in rules)
        {
            WatchdogRules.Add(rule);
        }

        Services.WatchdogRuleStore.Save([.. WatchdogRules]);
        Watchdog.UpdateRules([.. WatchdogRules]);
        Program.DiagnosticLog?.Information($"Saved {WatchdogRules.Count} watchdog rules.");
    }

    private void LoadMonitorRules()
    {
        VariableMonitor.UpdateRules([.. Services.VariableMonitorRuleStore.Load()]);
    }

    internal void SaveMonitorRules(VariableMonitorRule[] rules)
    {
        Services.VariableMonitorRuleStore.Save(rules);
        VariableMonitor.UpdateRules(rules);
        Program.DiagnosticLog?.Information($"Saved {rules.Length} monitor rules.");
    }

    private void SaveSettings()
    {
        if (_isLoadingSettings)
        {
            return;
        }

        try
        {
            AppSettingsService.Save(CaptureConfiguration());
        }
        catch (Exception exception)
        {
            Program.DiagnosticLog?.Warning($"Failed to save settings. {exception.Message}");
        }
    }

    private sealed record ConfigurationSnapshot(
        int BaudRate,
        int DataBits,
        StopBits StopBits,
        Parity Parity,
        Handshake Handshake,
        string EncodingName,
        ReceiveDisplayMode ReceiveMode,
        bool TimestampEnabled,
        bool LoggingEnabled,
        string LogDirectory,
        SendMode SendMode,
        NewlinePolicy Newline,
        string TimestampFormat = "HH:mm:ss.fff",
        int LogRotationMegabytes = 40,
        bool LogRotationEnabled = true,
        int DisplayBudgetMegabytes = 64,
        bool PrivateMemoryMonitorEnabled = false,
        int PrivateMemoryThresholdMiB = 1024,
        string LogFileNameFormat = "{Port}-{yyyy}-{MM}-{dd} {HH}-{mm}-{ss}.{fff}",
        bool FreezeAfterSend = false,
        bool SendPrefixEnabled = true,
        string SendPrefix = "TX > ",
        bool PauseFollowOnMouseWheel = true,
        bool PauseFollowOnFocus = false,
        bool ShowPauseHint = true,
        bool AutoBackupEnabled = true,
        int AutoBackupPeriodDays = 7,
        bool PreventSleep = false,
        bool CloseToTaskbar = false,
        bool WordWrap = false,
        bool ShowLineNumbers = false,
        bool HighlightCurrentLine = true,
        bool ShowControlCharacters = false,
        bool ShowSpaces = false,
        bool ShowTabs = false,
        double LogFontSize = 14,
        string LogFontFamily = "Cascadia Mono",
        string Language = "",
        string ThemeMode = "Dark",
        bool ShowPortType = true,
        double SearchOpacity = 1d,
        bool IsSidebarVisible = true,
        PortSortMode PortSortMode = PortSortMode.NameAscending,
        bool ShowHiddenPorts = false,
        bool ShowSerialPorts = true,
        bool ShowVirtualPorts = true,
        bool BackgroundImageEnabled = false,
        string? BackgroundImagePath = null,
        string? BackgroundImageFolderPath = null,
        BackgroundImagePlaybackMode BackgroundImagePlaybackMode = BackgroundImagePlaybackMode.SingleImage,
        int BackgroundImageIntervalSeconds = 300,
        double BackgroundImageOpacity = 0.18d,
        int TelnetPort = 23,
        bool TelnetAllowRemote = false,
        bool TelnetAuthenticationEnabled = false,
        string? TelnetUsername = null,
        List<int>? CustomBaudRates = null,
        Dictionary<string, PortSettingSnapshot>? PortOverrides = null,
        List<string>? HiddenPorts = null,
        List<string>? CommandTargetPortNames = null,
        List<string>? RightPanePorts = null,
        SplitLayoutOrientation SplitOrientation = SplitLayoutOrientation.Vertical,
        double SplitterRatio = 0.5d,
        List<string>? SessionOrder = null,
        List<string>? OpenSessionPorts = null,
        string? SelectedSessionPort = null,
        string? SelectedRightSessionPort = null);

    private static string[] NormalizePortNames(IEnumerable<string>? portNames) => [.. (portNames ?? [])
        .Where(name => !string.IsNullOrWhiteSpace(name))
        .Select(name => name.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
        .ThenBy(name => name, StringComparer.Ordinal)];

    private sealed record PortSettingSnapshot(
        int BaudRate,
        int DataBits,
        StopBits StopBits,
        Parity Parity,
        Handshake Handshake,
        string EncodingName,
        bool? DtrEnable = null,
        bool? RtsEnable = null,
        bool? DiscardNull = null,
        SendMode? SendMode = null,
        NewlinePolicy? Newline = null,
        ReceiveDisplayMode? ReceiveMode = null,
        bool? TimestampEnabled = null,
        bool? LoggingEnabled = null,
        string? LogDirectory = null,
        int? LogRotationMegabytes = null,
        bool? LogRotationEnabled = null,
        int? DisplayBudgetMegabytes = null,
        string? LogFileNameFormat = null,
        bool? SendPrefixEnabled = null,
        string? SendPrefix = null,
        bool? FollowEnd = null,
        bool? FilterEnabled = null,
        bool? AutoReconnect = null,
        Guid? HighlightRuleProjectId = null);

    private sealed record PortSessionPreferences(
        ReceiveDisplayMode ReceiveMode,
        bool TimestampEnabled,
        bool LoggingEnabled,
        string LogDirectory,
        long LogRotationBytes,
        bool LogRotationEnabled,
        int DisplayBudgetBytes,
        string LogFileNameFormat,
        bool SendPrefixEnabled,
        string SendPrefix,
        string TimestampFormat,
        bool FollowEnd,
        bool FilterEnabled,
        SendMode SendMode,
        NewlinePolicy Newline,
        Guid? HighlightRuleProjectId);

    private void OnCompositionRendering(object? sender, EventArgs e)
    {
        if (e is not RenderingEventArgs { RenderingTime: TimeSpan renderingTime } ||
            renderingTime - _lastRenderTime < MinimumRenderInterval)
        {
            return;
        }

        _lastRenderTime = renderingTime;
        OnRenderTick(renderingTime);
    }

    private void OnRenderTick(TimeSpan renderingTime)
    {
        HashSet<SessionViewModel> sessionsToProject = [];
        if (SelectedSession is not null)
        {
            sessionsToProject.Add(SelectedSession);
        }

        if (SelectedRightSession is not null)
        {
            sessionsToProject.Add(SelectedRightSession);
        }

        bool commandStateChanged = false;
        foreach (SessionViewModel session in sessionsToProject)
        {
            commandStateChanged |= session.PullDisplaySnapshot(session.Search.IsOpen);
        }
        if (renderingTime - _lastStatusRefreshTime < StatusRefreshInterval)
        {
            if (commandStateChanged)
            {
                NotifyCommandStates();
            }
            return;
        }

        _lastStatusRefreshTime = renderingTime;
        foreach (PortItemViewModel port in AvailablePorts)
        {
            port.Update(Sessions.FirstOrDefault(session => string.Equals(session.PortName, port.PortName, StringComparison.OrdinalIgnoreCase)));
        }
        _serialParametersWindow?.RefreshTransportState();

        if (commandStateChanged)
        {
            NotifyCommandStates();
        }
    }

    private void RemoveRightSession(SessionViewModel session)
    {
        session.IsInRightPane = false;
        RightSessions.Remove(session);
        if (SelectedSession is null || ReferenceEquals(SelectedSession, session))
        {
            SelectedSession = Sessions.FirstOrDefault(item => item.IsOpen && !ReferenceEquals(item, session));
        }

        if (SelectedSession is not null)
        {
            SelectedPortItem = AvailablePorts.FirstOrDefault(item =>
                string.Equals(item.PortName, SelectedSession.PortName, StringComparison.OrdinalIgnoreCase));
        }
    }

    private void OnSessionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasSessions));
        MarkSettingsDirty();
    }

    private void OnRightSessionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(IsSplitView));
        if (SelectedRightSession is null || !RightSessions.Contains(SelectedRightSession))
        {
            SelectedRightSession = RightSessions.LastOrDefault();
        }
        MarkSettingsDirty();
    }

    private void OnSettingsSaveTick(object? sender, EventArgs e)
    {
        if (_settingsDirty)
        {
            _settingsDirty = false;
            SaveSettings();
        }
    }

    private async void OnPortSettingsApplyTick(object? sender, EventArgs e)
    {
        _portSettingsApplyTimer.Stop();
        SessionViewModel? session = _portSettingsTargetSession;
        if (_isLoadingSettings || session is null)
        {
            return;
        }

        if (session.IsBusy)
        {
            _portSettingsApplyTimer.Start();
            return;
        }

        await ApplyPendingPortSettingsAsync(session);
    }

    private async Task ApplyPortSettingsAsync(SessionViewModel session, SerialPortSettings updated)
    {
        try
        {
            await session.ApplySettingsAsync(updated);
            RememberPortOverride(session.PortName, updated);
            StatusMessage = string.Empty;
            Program.DiagnosticLog?.Information($"Runtime serial settings updated. Port={session.PortName}; Baud={updated.BaudRate}; DataBits={updated.DataBits}; StopBits={updated.StopBits}; Parity={updated.Parity}; Handshake={updated.Handshake}; Encoding={updated.EncodingName}; DTR={updated.DtrEnable}; RTS={updated.RtsEnable}; DiscardNull={updated.DiscardNull}");
        }
        catch (Exception exception)
        {
            StatusMessage = GetResourceString("Status.InvalidPortSettings");
            Program.DiagnosticLog?.Error("Runtime serial settings update failed.", exception);
        }
    }

    private void SchedulePortSettingsApply()
    {
        SessionViewModel? session = _portSettingsTargetSession;
        if (_isLoadingSettings || session is null)
        {
            return;
        }

        _portSettingsApplyPending = true;
        _portSettingsApplyTimer.Stop();
        _portSettingsApplyTimer.Start();
    }

    private async Task<bool> FlushPortSettingsAsync()
    {
        _portSettingsApplyTimer.Stop();
        SessionViewModel? session = _portSettingsTargetSession;
        if (!_portSettingsApplyPending || session is null)
        {
            return true;
        }

        if (session.IsBusy)
        {
            StatusMessage = GetResourceString("Status.PortSettingsBusy");
            return false;
        }

        await ApplyPendingPortSettingsAsync(session);
        return !_portSettingsApplyPending;
    }

    private async Task ApplyPendingPortSettingsAsync(SessionViewModel session)
    {
        SerialPortSettings current = session.WorkspaceSession.Settings;
        SerialPortSettings updated = current with
        {
            BaudRate = SerialParameterBaudRate,
            DataBits = SerialParameterDataBits,
            StopBits = SerialParameterStopBits,
            Parity = SerialParameterParity,
            Handshake = SerialParameterHandshake,
            EncodingName = SerialParameterEncodingName,
            DtrEnable = SerialParameterDtrEnable,
            RtsEnable = SerialParameterRtsEnable,
            DiscardNull = SerialParameterDiscardNull,
        };
        _portSettingsApplyPending = false;
        await ApplyPortSettingsAsync(session, updated);
        _portSettingsApplyPending = session.WorkspaceSession.Settings != updated;
    }

    private void MarkSettingsDirty()
    {
        if (_isLoadingSettings)
        {
            return;
        }

        _settingsDirty = true;
        _settingsSaveTimer.Stop();
        _settingsSaveTimer.Start();
    }

    private void EnsureActiveBaudRatesPresent()
    {
        EnsureBaudRatePresent(BaudRate);
        EnsureBaudRatePresent(SerialParameterBaudRate);
        foreach (SessionViewModel session in Sessions)
        {
            EnsureBaudRatePresent(session.BaudRate);
        }
    }

    private void RestoreDefaultBaudRates()
    {
        HashSet<int> desired = [.. DefaultBaudRates, .. Sessions.Select(session => session.BaudRate)];
        foreach (int value in BaudRates.Where(value => !desired.Contains(value)).ToArray())
        {
            BaudRates.Remove(value);
        }

        foreach (int value in desired.Order())
        {
            if (!BaudRates.Contains(value))
            {
                BaudRates.Add(value);
            }
        }

        int[] ordered = [.. BaudRates.Order()];
        for (int targetIndex = 0; targetIndex < ordered.Length; targetIndex++)
        {
            int currentIndex = BaudRates.IndexOf(ordered[targetIndex]);
            if (currentIndex != targetIndex)
            {
                BaudRates.Move(currentIndex, targetIndex);
            }
        }
    }

    private static string GetDefaultLogDirectory() => Path.Combine(AppContext.BaseDirectory, "Logs");

    private void EnsureBaudRatePresent(int baudRate)
    {
        if (baudRate <= 0 || BaudRates.Contains(baudRate))
        {
            return;
        }

        BaudRates.Add(baudRate);
        List<int> ordered = [.. BaudRates.Order()];
        BaudRates.Clear();
        foreach (int value in ordered)
        {
            BaudRates.Add(value);
        }
    }

    partial void OnBaudRateChanged(int value)
    {
        MarkSettingsDirty();
        SchedulePortSettingsApply();
    }

    partial void OnSerialParameterSendModeChanged(SendMode value)
    {
        if (_portSettingsTargetSession is not null)
        {
            _portSettingsTargetSession.SendMode = value;
            RememberPortOverride(_portSettingsTargetSession.PortName);
        }
        else if (!_isLoadingSettings)
        {
            DefaultSendMode = value;
        }
    }

    partial void OnSerialParameterNewlineChanged(NewlinePolicy value)
    {
        if (_portSettingsTargetSession is not null)
        {
            _portSettingsTargetSession.Newline = value;
            RememberPortOverride(_portSettingsTargetSession.PortName);
        }
        else if (!_isLoadingSettings)
        {
            DefaultNewline = value;
        }
    }

    partial void OnSerialParameterInterpretSendEscapesChanged(bool value)
    {
        if (_portSettingsTargetSession is not null)
        {
            _portSettingsTargetSession.InterpretSendEscapes = value;
        }
    }

    partial void OnSerialParameterTimedSendEnabledChanged(bool value)
    {
        if (_portSettingsTargetSession is not null)
        {
            _portSettingsTargetSession.TimedSendEnabled = value;
        }
    }

    partial void OnSerialParameterTimedSendIntervalMillisecondsChanged(int value)
    {
        if (_portSettingsTargetSession is not null)
        {
            _portSettingsTargetSession.TimedSendIntervalMilliseconds = Math.Clamp(value, 50, 86_400_000);
        }
    }

    partial void OnSerialParameterReceiveModeChanged(ReceiveDisplayMode value) => ApplySessionPreferenceChange(session => session.ReceiveMode = value);
    partial void OnSerialParameterTimestampEnabledChanged(bool value) => ApplySessionPreferenceChange(session => session.TimestampEnabled = value);
    partial void OnSerialParameterLoggingEnabledChanged(bool value) => ApplySessionPreferenceChange(session => session.LoggingEnabled = value);
    partial void OnSerialParameterFollowEndChanged(bool value) => ApplySessionPreferenceChange(session => session.FollowEnd = value);
    partial void OnSerialParameterFilterEnabledChanged(bool value) => ApplySessionPreferenceChange(session => session.FilterEnabled = value);

    private void ApplySessionPreferenceChange(Action<SessionViewModel> update)
    {
        if (_isLoadingSettings || _portSettingsTargetSession is null)
        {
            return;
        }

        update(_portSettingsTargetSession);
        RememberPortOverride(_portSettingsTargetSession.PortName);
        if (_portSettingsTargetSession.IsOpen)
        {
            StatusMessage = GetResourceString("Status.SessionSettingRequiresReopen");
        }
    }

    partial void OnSerialParameterBaudRateChanged(int value) => ApplySerialParameterChange(() => BaudRate = value);
    partial void OnSerialParameterDataBitsChanged(int value) => ApplySerialParameterChange(() => DataBits = value);
    partial void OnSerialParameterStopBitsChanged(StopBits value) => ApplySerialParameterChange(() => StopBits = value);
    partial void OnSerialParameterParityChanged(Parity value) => ApplySerialParameterChange(() => Parity = value);
    partial void OnSerialParameterHandshakeChanged(Handshake value) => ApplySerialParameterChange(() => Handshake = value);
    partial void OnSerialParameterEncodingNameChanged(string value) => ApplySerialParameterChange(() => EncodingName = value);
    partial void OnSerialParameterDtrEnableChanged(bool value) => ApplySerialParameterChange(null);
    partial void OnSerialParameterRtsEnableChanged(bool value) => ApplySerialParameterChange(null);
    partial void OnSerialParameterDiscardNullChanged(bool value) => ApplySerialParameterChange(null);

    partial void OnSerialParameterAutoReconnectChanged(bool value)
    {
        if (_portSettingsTargetSession is not null)
        {
            _portSettingsTargetSession.AutoReconnect = value;
            RememberPortOverride(_portSettingsTargetSession.PortName);
        }
    }

    private void ApplySerialParameterChange(Action? updateDefault)
    {
        if (_isLoadingSettings)
        {
            return;
        }

        if (_portSettingsTargetSession is null)
        {
            updateDefault?.Invoke();
            return;
        }

        SchedulePortSettingsApply();
    }

    partial void OnDataBitsChanged(int value)
    {
        MarkSettingsDirty();
        SchedulePortSettingsApply();
    }

    partial void OnStopBitsChanged(StopBits value)
    {
        MarkSettingsDirty();
        SchedulePortSettingsApply();
    }

    partial void OnParityChanged(Parity value)
    {
        MarkSettingsDirty();
        SchedulePortSettingsApply();
    }

    partial void OnHandshakeChanged(Handshake value)
    {
        MarkSettingsDirty();
        SchedulePortSettingsApply();
    }

    partial void OnEncodingNameChanged(string value)
    {
        MarkSettingsDirty();
        SchedulePortSettingsApply();
    }

    partial void OnReceiveModeChanged(ReceiveDisplayMode value) => MarkSettingsDirty();

    partial void OnTimestampEnabledChanged(bool value) => MarkSettingsDirty();

    partial void OnTimestampFormatChanged(string value) => MarkSettingsDirty();

    partial void OnLoggingEnabledChanged(bool value) => MarkSettingsDirty();

    partial void OnLogDirectoryChanged(string value) => MarkSettingsDirty();

    partial void OnDefaultSendModeChanged(SendMode value) => MarkSettingsDirty();

    partial void OnDefaultNewlineChanged(NewlinePolicy value) => MarkSettingsDirty();

    partial void OnLogRotationMegabytesChanged(int value) => MarkSettingsDirty();
    partial void OnLogRotationEnabledChanged(bool value) => MarkSettingsDirty();
    partial void OnDisplayBudgetMegabytesChanged(int value) => MarkSettingsDirty();
    partial void OnPrivateMemoryMonitorEnabledChanged(bool value)
    {
        if (!value)
        {
            _privateMemoryThresholdWasReached = false;
            IsPrivateMemoryThresholdReached = false;
        }

        MarkSettingsDirty();
    }
    partial void OnPrivateMemoryThresholdMiBChanged(int value) => MarkSettingsDirty();
    partial void OnLogFileNameFormatChanged(string value)
    {
        OnPropertyChanged(nameof(LogFileNamePreview));
        MarkSettingsDirty();
    }
    partial void OnFreezeAfterSendChanged(bool value) => MarkSettingsDirty();
    partial void OnSendPrefixEnabledChanged(bool value) => MarkSettingsDirty();
    partial void OnSendPrefixChanged(string value) => MarkSettingsDirty();
    partial void OnPauseFollowOnMouseWheelChanged(bool value) => MarkSettingsDirty();
    partial void OnPauseFollowOnFocusChanged(bool value) => MarkSettingsDirty();
    partial void OnShowPauseHintChanged(bool value) => MarkSettingsDirty();
    partial void OnAutoBackupEnabledChanged(bool value) => MarkSettingsDirty();
    partial void OnAutoBackupPeriodDaysChanged(int value) => MarkSettingsDirty();
    partial void OnPreventSleepChanged(bool value)
    {
        MarkSettingsDirty();
        SystemPowerService.SetPreventSleep(value);
    }
    partial void OnCloseToTaskbarChanged(bool value) => MarkSettingsDirty();

    partial void OnWordWrapChanged(bool value) => MarkSettingsDirty();

    partial void OnShowLineNumbersChanged(bool value) => MarkSettingsDirty();

    partial void OnHighlightCurrentLineChanged(bool value) => MarkSettingsDirty();

    partial void OnShowControlCharactersChanged(bool value) => MarkSettingsDirty();

    partial void OnShowSpacesChanged(bool value) => MarkSettingsDirty();

    partial void OnShowTabsChanged(bool value) => MarkSettingsDirty();

    partial void OnLogFontSizeChanged(double value) => MarkSettingsDirty();
    partial void OnLogFontFamilyChanged(string value) => MarkSettingsDirty();
    partial void OnShowPortTypeChanged(bool value) => MarkSettingsDirty();
    partial void OnSearchOpacityChanged(double value) => MarkSettingsDirty();
    partial void OnIsSidebarVisibleChanged(bool value) => MarkSettingsDirty();
    partial void OnPortSortModeChanged(PortSortMode value) => MarkSettingsDirty();
    partial void OnShowHiddenPortsChanged(bool value) => MarkSettingsDirty();

    partial void OnShowSerialPortsChanged(bool value)
    {
        MarkSettingsDirty();
        RebuildPortItems(SelectedPort);
    }

    partial void OnShowVirtualPortsChanged(bool value)
    {
        MarkSettingsDirty();
        RebuildPortItems(SelectedPort);
    }

    partial void OnBackgroundImageEnabledChanged(bool value)
    {
        RefreshBackgroundImagePlayback();
        MarkSettingsDirty();
    }

    partial void OnBackgroundImagePathChanged(string value)
    {
        if (BackgroundImagePlaybackMode == BackgroundImagePlaybackMode.SingleImage)
        {
            OnPropertyChanged(nameof(BackgroundImageSource));
        }
        MarkSettingsDirty();
    }

    partial void OnBackgroundImageFolderPathChanged(string value)
    {
        RefreshBackgroundImagePlayback();
        MarkSettingsDirty();
    }

    partial void OnBackgroundImagePlaybackModeChanged(BackgroundImagePlaybackMode value)
    {
        RefreshBackgroundImagePlayback();
        MarkSettingsDirty();
    }

    partial void OnBackgroundImageIntervalSecondsChanged(int value)
    {
        UpdateBackgroundImageTimer();
        MarkSettingsDirty();
    }

    partial void OnBackgroundImageOpacityChanged(double value) => MarkSettingsDirty();

    internal void ShowNextBackgroundImage() => AdvanceBackgroundImage();

    internal void ConfigureSingleBackgroundImage(string path)
    {
        BackgroundImagePath = path;
        BackgroundImagePlaybackMode = BackgroundImagePlaybackMode.SingleImage;
        BackgroundImageEnabled = true;
        OnPropertyChanged(nameof(BackgroundImageSource));
    }

    internal void ConfigureBackgroundImageFolder(string path)
    {
        BackgroundImageFolderPath = path;
        BackgroundImagePlaybackMode = BackgroundImagePlaybackMode.Sequential;
        BackgroundImageEnabled = true;
        RefreshBackgroundImagePlayback();
        OnPropertyChanged(nameof(BackgroundImageSource));
    }

    internal void SetBackgroundImagePluginEnabled(bool enabled)
    {
        BackgroundImageEnabled = enabled;
        RefreshBackgroundImagePlayback();
        OnPropertyChanged(nameof(BackgroundImageSource));
    }

    private void RefreshBackgroundImagePlayback()
    {
        _backgroundImageTimer.Stop();
        if (BackgroundImagePlaybackMode == BackgroundImagePlaybackMode.SingleImage)
        {
            _backgroundImagePlaylist = [];
            _backgroundImageIndex = -1;
            CurrentBackgroundImagePath = string.Empty;
            OnPropertyChanged(nameof(BackgroundImageSource));
            return;
        }

        _backgroundImagePlaylist = GetBackgroundImages(BackgroundImageFolderPath);
        _backgroundImageIndex = -1;
        AdvanceBackgroundImage();
        UpdateBackgroundImageTimer();
        OnPropertyChanged(nameof(BackgroundImageSource));
    }

    private void UpdateBackgroundImageTimer()
    {
        _backgroundImageTimer.Stop();
        if (!BackgroundImageEnabled ||
            BackgroundImagePlaybackMode == BackgroundImagePlaybackMode.SingleImage ||
            _backgroundImagePlaylist.Length < 2)
        {
            return;
        }

        _backgroundImageTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(BackgroundImageIntervalSeconds, 1, 86_400));
        _backgroundImageTimer.Start();
    }

    private void OnBackgroundImageTimerTick(object? sender, EventArgs e) => AdvanceBackgroundImage();

    private void AdvanceBackgroundImage()
    {
        if (_backgroundImagePlaylist.Length == 0)
        {
            CurrentBackgroundImagePath = string.Empty;
            return;
        }

        if (BackgroundImagePlaybackMode == BackgroundImagePlaybackMode.Random && _backgroundImagePlaylist.Length > 1)
        {
            int next;
            do
            {
                next = Random.Shared.Next(_backgroundImagePlaylist.Length);
            }
            while (next == _backgroundImageIndex);
            _backgroundImageIndex = next;
        }
        else
        {
            _backgroundImageIndex = (_backgroundImageIndex + 1) % _backgroundImagePlaylist.Length;
        }

        CurrentBackgroundImagePath = _backgroundImagePlaylist[_backgroundImageIndex];
    }

    private static string[] GetBackgroundImages(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return [];
        }

        HashSet<string> extensions = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".bmp", ".webp" };
        return [.. Directory.EnumerateFiles(directory)
            .Where(path => extensions.Contains(Path.GetExtension(path)))
            .Order(StringComparer.OrdinalIgnoreCase)];
    }
    partial void OnTelnetPortChanged(int value) => MarkSettingsDirty();
    partial void OnTelnetAllowRemoteChanged(bool value) => MarkSettingsDirty();

    partial void OnTelnetAuthenticationEnabledChanged(bool value) => MarkSettingsDirty();

    partial void OnTelnetUsernameChanged(string value) => MarkSettingsDirty();

    partial void OnSplitOrientationChanged(SplitLayoutOrientation value) => MarkSettingsDirty();

    partial void OnSplitterRatioChanged(double value) => MarkSettingsDirty();

    private void OnPrivateMemorySampled(object? sender, PrivateMemoryThresholdSnapshot snapshot) =>
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            PrivateMemoryBytes = snapshot.PrivateMemoryBytes;
            IsPrivateMemoryThresholdReached = snapshot.IsThresholdReached;
            if (!snapshot.IsThresholdReached)
            {
                _privateMemoryThresholdWasReached = false;
            }
        });

    private void OnPrivateMemoryThresholdReached(object? sender, PrivateMemoryThresholdSnapshot snapshot) =>
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            PrivateMemoryBytes = snapshot.PrivateMemoryBytes;
            IsPrivateMemoryThresholdReached = true;
            if (_privateMemoryThresholdWasReached)
            {
                return;
            }

            _privateMemoryThresholdWasReached = true;
            double currentMiB = snapshot.PrivateMemoryBytes / 1024d / 1024d;
            double thresholdMiB = snapshot.ThresholdBytes / 1024d / 1024d;
            StatusMessage = GetResourceString("Status.PrivateMemoryThresholdReached")
                .Replace("{0}", currentMiB.ToString("0.0", CultureInfo.CurrentCulture), StringComparison.Ordinal)
                .Replace("{1}", thresholdMiB.ToString("0", CultureInfo.CurrentCulture), StringComparison.Ordinal);
            Program.DiagnosticLog?.Warning($"Private memory threshold reached. CurrentMiB={currentMiB:0.0}; ThresholdMiB={thresholdMiB:0}; no process action was taken.");
        });

    private async Task TogglePortAsync(PortItemViewModel port)
    {
        SelectedPortItem = port;
        SessionViewModel? session = Sessions.FirstOrDefault(
            item => string.Equals(item.PortName, port.PortName, StringComparison.OrdinalIgnoreCase));
        if (session?.IsOpen == true)
        {
            SelectedSession = session;
            await CloseAsync();
        }
        else
        {
            await OpenAsync();
        }
    }

    private async Task<SessionViewModel?> EnsureSessionOpenAsync(string portName)
    {
        SelectedPortItem = AvailablePorts.FirstOrDefault(
            item => string.Equals(item.PortName, portName, StringComparison.OrdinalIgnoreCase));
        SessionViewModel? session = Sessions.FirstOrDefault(
            item => string.Equals(item.PortName, portName, StringComparison.OrdinalIgnoreCase));
        if (session?.IsOpen != true)
        {
            await OpenAsync();
            session = Sessions.FirstOrDefault(item =>
                string.Equals(item.PortName, portName, StringComparison.OrdinalIgnoreCase));
        }

        return session is { IsOpen: true } ? session : null;
    }

    private void NotifyCommandStates()
    {
        OpenCommand.NotifyCanExecuteChanged();
        CloseCommand.NotifyCanExecuteChanged();
        SendCommand.NotifyCanExecuteChanged();
    }

    internal void OpenSearchFor(SessionViewModel session)
    {
        ArgumentNullException.ThrowIfNull(session);
        ActivateLogSession(session);
        session.Search.OpenCommand.Execute(null);
    }

    private static string PreviewLogFileName(string format, string portName)
    {
        DateTimeOffset sample = new(2026, 8, 31, 10, 7, 42, 813, TimeSpan.Zero);
        string value = (string.IsNullOrWhiteSpace(format) ? "{Port}-{yyyy}-{MM}-{dd} {HH}-{mm}-{ss}.{fff}" : format)
            .Replace("{Port}", portName, StringComparison.Ordinal)
            .Replace("{yyyy}", sample.ToString("yyyy", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{MM}", sample.ToString("MM", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{dd}", sample.ToString("dd", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{HH}", sample.ToString("HH", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{mm}", sample.ToString("mm", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{ss}", sample.ToString("ss", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{fff}", sample.ToString("fff", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{yyyyMMdd}", sample.ToString("yyyyMMdd", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{HHmmss}", sample.ToString("HHmmss", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{Segment}", "0000", StringComparison.Ordinal);
        return string.Concat(value.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character)) + ".txt";
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

public enum BackgroundImagePlaybackMode
{
    SingleImage,
    Sequential,
    Random,
}
