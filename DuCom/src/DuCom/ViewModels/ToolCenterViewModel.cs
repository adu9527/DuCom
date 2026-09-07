using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DuCom.Core.Sending;
using DuCom.Core.Telnet;
using DuCom.Services;
using DuCom.Services.Shortcuts;
using Microsoft.Win32;

namespace DuCom.ViewModels;

public partial class ToolCenterViewModel : ObservableObject, IAsyncDisposable
{
    private readonly Process _process = Process.GetCurrentProcess();
    private readonly DispatcherTimer _monitorTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly TelnetBridgeService _telnetBridge;
    private readonly ShortcutManager _shortcutManager;
    private readonly MainViewModel? _mainViewModel;
    private TimeSpan _lastCpuTime;
    private DateTimeOffset _lastSample = DateTimeOffset.Now;

    public ToolCenterViewModel(
        ShortcutManager? shortcutManager = null,
        MainViewModel? mainViewModel = null,
        TelnetBridgeService? telnetBridge = null)
    {
        _shortcutManager = shortcutManager ?? new ShortcutManager();
        if (shortcutManager is null)
        {
            _shortcutManager.RegisterDefaultActions();
        }

        _mainViewModel = mainViewModel;
        _telnetBridge = telnetBridge ?? throw new ArgumentNullException(nameof(telnetBridge));
        if (_mainViewModel is not null)
        {
            TelnetPort = _mainViewModel.TelnetPort;
            TelnetAllowRemote = _mainViewModel.TelnetAllowRemote;
            TelnetAuthenticationEnabled = _mainViewModel.TelnetAuthenticationEnabled;
            TelnetUsername = _mainViewModel.TelnetUsername;
            BackgroundImageEnabled = _mainViewModel.BackgroundImageEnabled;
            BackgroundImagePath = _mainViewModel.BackgroundImagePath;
            BackgroundImageFolderPath = _mainViewModel.BackgroundImageFolderPath;
            BackgroundImagePlaybackMode = _mainViewModel.BackgroundImagePlaybackMode;
            BackgroundImageIntervalSeconds = _mainViewModel.BackgroundImageIntervalSeconds;
            BackgroundImageOpacity = _mainViewModel.BackgroundImageOpacity;
            ShowMemoryMonitor = _mainViewModel.ShowMemoryMonitor;
            MemoryRefreshInterval = _mainViewModel.MemoryRefreshInterval;
            SystemMemoryRefreshInterval = _mainViewModel.SystemMemoryRefreshInterval;
        }

        RefreshShortcutRows();
        AsciiRows = Enumerable.Range(0, 128)
            .Select(value => new AsciiRow(value, $"0x{value:X2}", value is < 32 or 127 ? ControlName(value) : ((char)value).ToString()))
            .ToArray();
        PluginDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DuCom", "Plugins");
        _monitorTimer.Tick += OnMonitorTick;
        _monitorTimer.Start();
        _telnetBridge.StatusChanged += OnTelnetStatusChanged;
        _telnetBridge.Diagnostic += OnTelnetDiagnostic;
        RefreshPlugins();
        RefreshVirtualPorts();
        UpdateMonitor();
        UpdateTelnetStatus();
        IsBridgeBound = _telnetBridge.IsBound;
        if (IsBridgeBound)
        {
            BridgePortName = _telnetBridge.BoundPortName ?? string.Empty;
            TelnetBridgeStatus = GetResourceString("Tools.BridgeBound")
                .Replace("{0}", BridgePortName, StringComparison.Ordinal);
        }
        RefreshSendHistoryList();
        LoadWatchdogRows();
        LoadMonitorRows();
        _ = RefreshCom0ComPairsAsync();
    }

    private void LoadMonitorRows()
    {
        MonitorRows.Clear();
        if (_mainViewModel is null)
        {
            return;
        }

        foreach (DuCom.Core.Diagnostics.VariableMonitorRule rule in _mainViewModel.VariableMonitor.Rules)
        {
            MonitorRows.Add(MonitorRuleRow.From(rule));
        }
    }

    [RelayCommand]
    private void AddMonitorRule() => MonitorRows.Add(new MonitorRuleRow
    {
        Name = $"var {MonitorRows.Count + 1}",
        Pattern = @"(\d+(?:\.\d+)?)",
    });

    [RelayCommand]
    private void DeleteMonitorRule(MonitorRuleRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        MonitorRows.Remove(row);
    }

    [RelayCommand]
    private void SaveMonitorRules()
    {
        if (_mainViewModel is null)
        {
            return;
        }

        List<DuCom.Core.Diagnostics.VariableMonitorRule> rules = [];
        foreach (MonitorRuleRow row in MonitorRows)
        {
            if (string.IsNullOrWhiteSpace(row.Name) || string.IsNullOrWhiteSpace(row.Pattern))
            {
                continue;
            }

            rules.Add(row.ToRule());
        }

        _mainViewModel.SaveMonitorRules([.. rules]);
    }

    [RelayCommand]
    private void ExportMonitorCsv()
    {
        if (_mainViewModel is null)
        {
            return;
        }

        SaveFileDialog dialog = new()
        {
            Filter = "CSV (*.csv)|*.csv",
            FileName = "ducom-monitor.csv",
        };
        if (dialog.ShowDialog() == true)
        {
            File.WriteAllText(dialog.FileName, _mainViewModel.VariableMonitor.ExportCsv(), new System.Text.UTF8Encoding(false));
        }
    }

    private void RefreshMonitorValues()
    {
        if (_mainViewModel is null)
        {
            return;
        }

        MonitorValues.Clear();
        foreach ((DuCom.Core.Diagnostics.VariableMonitorRule rule, DuCom.Core.Diagnostics.VariableMonitorSample? sample) in _mainViewModel.VariableMonitor.GetRuleStates())
        {
            MonitorValues.Add(new MonitorValueRow
            {
                Name = rule.PortName is { Length: > 0 } ? $"{rule.Name} [{rule.PortName}]" : rule.Name,
                Value = sample?.Value ?? string.Empty,
                SampledAt = sample?.SampledAtUtc.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) ?? string.Empty,
                MatchCount = sample?.MatchCount ?? 0,
            });
        }
    }

    private static string GetResourceString(string key) =>
        Application.Current?.TryFindResource(key) as string ?? key;

    private void LoadWatchdogRows()
    {
        WatchdogRows.Clear();
        if (_mainViewModel is null)
        {
            return;
        }

        foreach (DuCom.Core.Diagnostics.WatchdogRule rule in _mainViewModel.WatchdogRules)
        {
            WatchdogRows.Add(WatchdogRuleRow.From(rule));
        }
    }

    [RelayCommand]
    private void AddWatchdogRule() => WatchdogRows.Add(new WatchdogRuleRow
    {
        Name = $"rule {WatchdogRows.Count + 1}",
    });

    [RelayCommand]
    private void DeleteWatchdogRule(WatchdogRuleRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        WatchdogRows.Remove(row);
    }

    [RelayCommand]
    private void SaveWatchdogRules()
    {
        if (_mainViewModel is null)
        {
            return;
        }

        List<DuCom.Core.Diagnostics.WatchdogRule> rules = [];
        foreach (WatchdogRuleRow row in WatchdogRows)
        {
            if (string.IsNullOrWhiteSpace(row.Name) || string.IsNullOrWhiteSpace(row.Pattern))
            {
                continue;
            }

            rules.Add(row.ToRule());
        }

        _mainViewModel.SaveWatchdogRules([.. rules]);
    }

    public ObservableCollection<ShortcutRow> FilteredShortcuts { get; } = [];

    public IReadOnlyList<AsciiRow> AsciiRows { get; }

    public ObservableCollection<PluginRow> Plugins { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBackgroundPluginSelected))]
    [NotifyPropertyChangedFor(nameof(IsExternalPluginSelected))]
    public partial PluginRow? SelectedPlugin { get; set; }

    public bool IsBackgroundPluginSelected => SelectedPlugin?.IsBackgroundPlugin == true;

    public bool IsExternalPluginSelected => SelectedPlugin is { IsBackgroundPlugin: false };

    [ObservableProperty]
    public partial bool BackgroundImageEnabled { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BackgroundImageSource))]
    public partial string BackgroundImagePath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string BackgroundImageFolderPath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial BackgroundImagePlaybackMode BackgroundImagePlaybackMode { get; set; }

    public IReadOnlyList<BackgroundImagePlaybackMode> BackgroundImagePlaybackModes { get; } =
        [BackgroundImagePlaybackMode.SingleImage, BackgroundImagePlaybackMode.Sequential, BackgroundImagePlaybackMode.Random];

    [ObservableProperty]
    public partial int BackgroundImageIntervalSeconds { get; set; } = 30;

    [ObservableProperty]
    public partial double BackgroundImageOpacity { get; set; } = 0.18d;

    public ImageSource? BackgroundImageSource => _mainViewModel?.BackgroundImageSource;

    public ObservableCollection<string> VirtualPorts { get; } = [];

    public ObservableCollection<DuCom.Core.Processes.Com0ComPortPair> Com0ComPairs { get; } = [];

    [ObservableProperty]
    public partial DuCom.Core.Processes.Com0ComPortPair? SelectedCom0ComPair { get; set; }

    [ObservableProperty]
    public partial string NewPairPortA { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NewPairPortB { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool PairEmuBR { get; set; }

    [ObservableProperty]
    public partial bool PairEmuOverrun { get; set; }

    [ObservableProperty]
    public partial bool PairHiddenMode { get; set; }

    [ObservableProperty]
    public partial bool PairPlugInMode { get; set; }

    [ObservableProperty]
    public partial bool PairExclusiveMode { get; set; }

    [ObservableProperty]
    public partial string PairEmuNoise { get; set; } = "0";

    [ObservableProperty]
    public partial string PairRtto { get; set; } = "0";

    [ObservableProperty]
    public partial string PairRito { get; set; } = "0";

    [ObservableProperty]
    public partial string Com0ComStatus { get; set; } = string.Empty;

    public string PluginDirectory { get; }

    [ObservableProperty]
    public partial int SelectedTabIndex { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilteredShortcuts))]
    public partial string ShortcutSearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsEditingShortcut { get; set; }

    [ObservableProperty]
    public partial string EditingActionName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string EditingGestureText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string EditingErrorMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CpuText { get; private set; } = "0.0%";

    [ObservableProperty]
    public partial string MemoryText { get; private set; } = "--";

    [ObservableProperty]
    public partial string RuntimeText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string PrivateMemoryMonitorStatus { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsPrivateMemoryWarningVisible { get; private set; }

    [ObservableProperty]
    public partial bool ShowMemoryMonitor { get; set; } = true;

    [ObservableProperty]
    public partial int MemoryRefreshInterval { get; set; } = 2;

    [ObservableProperty]
    public partial int SystemMemoryRefreshInterval { get; set; } = 5;

    [ObservableProperty]
    public partial bool IsCom0ComAvailable { get; private set; }

    [ObservableProperty]
    public partial string Com0ComPath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int TelnetPort { get; set; } = 23;

    [ObservableProperty]
    public partial bool TelnetAllowRemote { get; set; }

    [ObservableProperty]
    public partial bool TelnetAuthenticationEnabled { get; set; }

    [ObservableProperty]
    public partial string TelnetUsername { get; set; } = string.Empty;

    public string TelnetPassword { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string TelnetListenAddressText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsTelnetRunning { get; private set; }

    [ObservableProperty]
    public partial int TelnetClientCount { get; private set; }

    [ObservableProperty]
    public partial bool IsBridgeBound { get; private set; }

    [ObservableProperty]
    public partial string TelnetBridgeStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string BridgePortName { get; set; } = string.Empty;

    public ObservableCollection<string> BridgePortOptions { get; } = [];

    public ObservableCollection<string> TelnetClients { get; } = [];

    public ObservableCollection<string> SendHistoryList { get; } = [];

    [ObservableProperty]
    public partial string SendHistorySearchText { get; set; } = string.Empty;

    public ObservableCollection<WatchdogRuleRow> WatchdogRows { get; } = [];

    public ObservableCollection<MonitorRuleRow> MonitorRows { get; } = [];

    public ObservableCollection<MonitorValueRow> MonitorValues { get; } = [];

    partial void OnSendHistorySearchTextChanged(string value) => RefreshSendHistoryList();

    partial void OnSelectedCom0ComPairChanged(DuCom.Core.Processes.Com0ComPortPair? value)
    {
        if (value is null)
        {
            return;
        }

        NewPairPortA = value.SideA.PortName;
        NewPairPortB = value.SideB.PortName;
        IReadOnlyDictionary<string, string> options = value.SideA.Options;
        PairEmuBR = ReadCom0ComBoolean(options, "EmuBR");
        PairEmuOverrun = ReadCom0ComBoolean(options, "EmuOverrun");
        PairHiddenMode = ReadCom0ComBoolean(options, "HiddenMode");
        PairPlugInMode = ReadCom0ComBoolean(options, "PlugInMode");
        PairExclusiveMode = ReadCom0ComBoolean(options, "ExclusiveMode");
        PairEmuNoise = ReadCom0ComValue(options, "EmuNoise", "0");
        PairRtto = ReadCom0ComValue(options, "AddRTTO", "0");
        PairRito = ReadCom0ComValue(options, "AddRITO", "0");
    }

    private static bool ReadCom0ComBoolean(IReadOnlyDictionary<string, string> options, string key) =>
        options.TryGetValue(key, out string? value) &&
        value is not null &&
        (value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
         value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
         value == "1");

    private static string ReadCom0ComValue(IReadOnlyDictionary<string, string> options, string key, string fallback) =>
        options.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value) ? value : fallback;

    internal void RefreshSendHistoryList()
    {
        SendHistoryList.Clear();
        if (_mainViewModel is null)
        {
            return;
        }

        foreach (string entry in _mainViewModel.SearchSendHistoryEntries(SendHistorySearchText))
        {
            SendHistoryList.Add(entry);
        }
    }

    [RelayCommand]
    private void UseSendHistoryEntry(string? entry)
    {
        if (!string.IsNullOrEmpty(entry))
        {
            _mainViewModel?.UseSendHistoryEntry(entry);
        }
    }

    [RelayCommand]
    private void DeleteSendHistoryEntry(string? entry)
    {
        if (string.IsNullOrEmpty(entry))
        {
            return;
        }

        _mainViewModel?.DeleteSendHistoryEntry(entry);
        RefreshSendHistoryList();
    }

    [RelayCommand]
    private void ClearSendHistory()
    {
        _mainViewModel?.ClearSendHistory();
        RefreshSendHistoryList();
    }

    [RelayCommand]
    private void OpenPluginFolder()
    {
        Directory.CreateDirectory(PluginDirectory);
        Process.Start(new ProcessStartInfo(PluginDirectory) { UseShellExecute = true });
    }

    [RelayCommand]
    private void RefreshPlugins()
    {
        Directory.CreateDirectory(PluginDirectory);
        string? selectedPath = SelectedPlugin?.Path;
        Plugins.Clear();
        Plugins.Add(new PluginRow(
            GetResourceString("Plugins.BackgroundImage.Name"),
            "Built-in",
            GetResourceString("Plugins.BackgroundImage.Description"),
            string.Empty,
            IsBackgroundPlugin: true));
        foreach (string file in Directory.GetFiles(PluginDirectory, "*.dll"))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            string version = "Unknown";
            try
            {
                version = AssemblyName.GetAssemblyName(file).Version?.ToString() ?? version;
            }
            catch (BadImageFormatException)
            {
                version = "Invalid .NET assembly";
            }

            Plugins.Add(new PluginRow(
                name,
                version,
                GetResourceString("Plugins.External.Description"),
                file,
                IsBackgroundPlugin: false));
        }

        SelectedPlugin = Plugins.FirstOrDefault(plugin => string.Equals(plugin.Path, selectedPath, StringComparison.OrdinalIgnoreCase))
            ?? Plugins.FirstOrDefault();
    }

    [RelayCommand]
    private void SelectBackgroundImage()
    {
        OpenFileDialog dialog = new()
        {
            Filter = GetResourceString("Plugins.BackgroundImage.Filter"),
            CheckFileExists = true,
            Multiselect = false,
        };
        if (dialog.ShowDialog() == true)
        {
            BackgroundImagePath = dialog.FileName;
            BackgroundImageEnabled = true;
        }
    }

    [RelayCommand]
    private void SelectBackgroundImageFolder()
    {
        OpenFolderDialog dialog = new()
        {
            Title = GetResourceString("Plugins.BackgroundImage.SelectFolder"),
            InitialDirectory = Directory.Exists(BackgroundImageFolderPath) ? BackgroundImageFolderPath : null,
        };
        if (dialog.ShowDialog() == true)
        {
            BackgroundImageFolderPath = dialog.FolderName;
            BackgroundImagePlaybackMode = BackgroundImagePlaybackMode.Sequential;
            BackgroundImageEnabled = true;
        }
    }

    [RelayCommand]
    private void ClearBackgroundImage()
    {
        BackgroundImageEnabled = false;
        BackgroundImagePath = string.Empty;
        BackgroundImageFolderPath = string.Empty;
    }

    [RelayCommand]
    private void ToggleBackgroundImage() => BackgroundImageEnabled = !BackgroundImageEnabled;

    [RelayCommand]
    private void ShowNextBackgroundImage()
    {
        _mainViewModel?.ShowNextBackgroundImage();
        OnPropertyChanged(nameof(BackgroundImageSource));
    }

    partial void OnBackgroundImageEnabledChanged(bool value)
    {
        if (_mainViewModel is not null)
        {
            _mainViewModel.BackgroundImageEnabled = value;
            OnPropertyChanged(nameof(BackgroundImageSource));
        }
    }

    partial void OnBackgroundImagePathChanged(string value)
    {
        if (_mainViewModel is not null)
        {
            _mainViewModel.BackgroundImagePath = value;
            OnPropertyChanged(nameof(BackgroundImageSource));
        }
    }

    partial void OnBackgroundImageFolderPathChanged(string value)
    {
        if (_mainViewModel is not null)
        {
            _mainViewModel.BackgroundImageFolderPath = value;
            OnPropertyChanged(nameof(BackgroundImageSource));
        }
    }

    partial void OnBackgroundImagePlaybackModeChanged(BackgroundImagePlaybackMode value)
    {
        if (_mainViewModel is not null)
        {
            _mainViewModel.BackgroundImagePlaybackMode = value;
            OnPropertyChanged(nameof(BackgroundImageSource));
        }
    }

    partial void OnBackgroundImageIntervalSecondsChanged(int value)
    {
        if (_mainViewModel is not null)
        {
            _mainViewModel.BackgroundImageIntervalSeconds = Math.Clamp(value, 1, 86_400);
        }
    }

    partial void OnBackgroundImageOpacityChanged(double value)
    {
        if (_mainViewModel is not null)
        {
            _mainViewModel.BackgroundImageOpacity = Math.Clamp(value, 0d, 1d);
        }
    }

    [RelayCommand]
    private void RefreshVirtualPorts()
    {
        VirtualPorts.Clear();
        IEnumerable<string> ports = _mainViewModel?.DiscoveredPortNames ?? [];
        foreach (string port in ports.Order(StringComparer.OrdinalIgnoreCase))
        {
            VirtualPorts.Add(port);
        }

        string[] candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "com0com", "setupc.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "com0com", "setupc.exe"),
        ];
        Com0ComPath = DuCom.Core.Processes.Com0ComParser.ResolveSetupcPath(
            candidates,
            Com0ComPreferencesService.LoadSetupcPath(),
            File.Exists);
        IsCom0ComAvailable = !string.IsNullOrEmpty(Com0ComPath);
    }

    [RelayCommand]
    private void BrowseCom0ComSetupc()
    {
        OpenFileDialog dialog = new()
        {
            Filter = GetResourceString("Tools.SetupcFileFilter"),
            CheckFileExists = true,
            FileName = "setupc.exe",
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        SelectCom0ComPath(dialog.FileName);
    }

    [RelayCommand]
    private void UseCom0ComPath()
    {
        SelectCom0ComPath(Com0ComPath);
    }

    private void SelectCom0ComPath(string path)
    {
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path.Trim());
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            Com0ComStatus = GetResourceString("Tools.Com0ComPathInvalid");
            IsCom0ComAvailable = false;
            return;
        }

        if (!File.Exists(fullPath) || !Path.GetFileName(fullPath).Equals("setupc.exe", StringComparison.OrdinalIgnoreCase))
        {
            Com0ComStatus = GetResourceString("Tools.Com0ComPathInvalid");
            IsCom0ComAvailable = false;
            return;
        }

        Com0ComPath = fullPath;
        IsCom0ComAvailable = true;
        Com0ComStatus = string.Empty;
        try
        {
            Com0ComPreferencesService.SaveSetupcPath(fullPath);
        }
        catch (Exception exception)
        {
            Program.DiagnosticLog?.Warning("Failed to save com0com path.", exception);
        }
    }

    [RelayCommand]
    private static void OpenCom0ComWebsite() =>
        Process.Start(new ProcessStartInfo("https://sourceforge.net/projects/com0com/") { UseShellExecute = true });

    [RelayCommand]
    private void OpenCom0ComFolder()
    {
        if (IsCom0ComAvailable)
        {
            Process.Start(new ProcessStartInfo(Path.GetDirectoryName(Com0ComPath)!) { UseShellExecute = true });
        }
    }

    [RelayCommand]
    private async Task RefreshCom0ComPairsAsync()
    {
        if (!IsCom0ComAvailable)
        {
            Com0ComStatus = GetResourceString("Tools.Com0ComMissing");
            return;
        }

        Com0ComCommandResult result = await Services.Com0ComService.RunAsync(Com0ComPath, "list");
        Com0ComPairs.Clear();
        if (result.Succeeded)
        {
            foreach (DuCom.Core.Processes.Com0ComPortPair pair in Services.Com0ComService.PairEntries(Services.Com0ComService.ParseList(result.Output)))
            {
                Com0ComPairs.Add(pair);
            }

            Com0ComStatus = string.Empty;
        }
        else
        {
            Com0ComStatus = result.Output;
            Program.DiagnosticLog?.Warning($"setupc list failed. {result.Output}");
        }
    }

    private string BuildPairOptions()
    {
        List<string> options =
        [
            $"EmuBR={(PairEmuBR ? "yes" : "no")}",
            $"EmuOverrun={(PairEmuOverrun ? "yes" : "no")}",
            $"HiddenMode={(PairHiddenMode ? "yes" : "no")}",
            $"PlugInMode={(PairPlugInMode ? "yes" : "no")}",
            $"ExclusiveMode={(PairExclusiveMode ? "yes" : "no")}",
        ];
        if (double.TryParse(PairEmuNoise, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double noise) && noise > 0)
        {
            options.Add($"EmuNoise={noise.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        }

        if (long.TryParse(PairRtto, out long rtto) && rtto > 0)
        {
            options.Add($"AddRTTO={rtto}");
        }

        if (long.TryParse(PairRito, out long rito) && rito > 0)
        {
            options.Add($"AddRITO={rito}");
        }

        return string.Join(",", options);
    }

    [RelayCommand]
    private async Task InstallPairAsync()
    {
        if (!IsCom0ComAvailable)
        {
            Com0ComStatus = GetResourceString("Tools.Com0ComMissing");
            return;
        }

        if (!Services.Com0ComService.IsValidPortName(NewPairPortA) || !Services.Com0ComService.IsValidPortName(NewPairPortB))
        {
            Com0ComStatus = GetResourceString("Tools.InvalidPortPair");
            return;
        }

        string options = BuildPairOptions();
        string arguments = $"install PortName={NewPairPortA.Trim().ToUpperInvariant()},{options} PortName={NewPairPortB.Trim().ToUpperInvariant()},{options}";
        Com0ComCommandResult result = await Services.Com0ComService.RunAsync(Com0ComPath, arguments);
        Com0ComStatus = result.Output;
        Program.DiagnosticLog?.Information($"setupc install: {arguments}; success={result.Succeeded}");
        if (result.Succeeded)
        {
            NewPairPortA = string.Empty;
            NewPairPortB = string.Empty;
            await RefreshCom0ComPairsAsync();
            RefreshVirtualPorts();
        }
    }

    [RelayCommand]
    private async Task RemovePairAsync(DuCom.Core.Processes.Com0ComPortPair pair)
    {
        ArgumentNullException.ThrowIfNull(pair);
        if (!IsCom0ComAvailable)
        {
            Com0ComStatus = GetResourceString("Tools.Com0ComMissing");
            return;
        }

        Com0ComCommandResult result = await Services.Com0ComService.RunAsync(Com0ComPath, $"remove {pair.PairNumber}");
        Com0ComStatus = result.Output;
        Program.DiagnosticLog?.Information($"setupc remove {pair.PairNumber}; success={result.Succeeded}");
        if (result.Succeeded)
        {
            await RefreshCom0ComPairsAsync();
            RefreshVirtualPorts();
        }
    }

    [RelayCommand]
    private async Task ApplyPairOptionsAsync(DuCom.Core.Processes.Com0ComPortPair pair)
    {
        ArgumentNullException.ThrowIfNull(pair);
        if (!IsCom0ComAvailable)
        {
            Com0ComStatus = GetResourceString("Tools.Com0ComMissing");
            return;
        }

        string options = BuildPairOptions();
        Com0ComCommandResult a = await Services.Com0ComService.RunAsync(Com0ComPath, $"change {pair.SideA.Id} {options}");
        Com0ComCommandResult b = await Services.Com0ComService.RunAsync(Com0ComPath, $"change {pair.SideB.Id} {options}");
        Com0ComStatus = (a.Output + " " + b.Output).Trim();
        Program.DiagnosticLog?.Information($"setupc change pair {pair.PairNumber}; successA={a.Succeeded}, successB={b.Succeeded}");
        if (a.Succeeded && b.Succeeded)
        {
            await RefreshCom0ComPairsAsync();
        }
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
    private async Task ToggleTelnetAsync()
    {
        if (_telnetBridge.IsRunning)
        {
            await _telnetBridge.StopAsync();
        }
        else
        {
            if (TelnetAllowRemote && !TelnetAuthenticationEnabled)
            {
                TelnetBridgeStatus = GetResourceString("Tools.TelnetRemoteAuthenticationRequired");
                return;
            }

            try
            {
                _telnetBridge.ConfigureAuthentication(new TelnetAuthenticationOptions(
                    TelnetAuthenticationEnabled,
                    TelnetUsername.Trim(),
                    TelnetPassword));
            }
            catch (ArgumentException)
            {
                TelnetBridgeStatus = GetResourceString("Tools.TelnetCredentialsRequired");
                return;
            }

            // Loopback by default; IPAddress.Any only through the explicit remote opt-in.
            _telnetBridge.Start(new TelnetListenOptions(TelnetPort, TelnetAllowRemote, TelnetAuthenticationEnabled));
        }

        UpdateTelnetStatus();
    }

    internal async Task SmokeTelnetAsync()
    {
        int originalPort = TelnetPort;
        bool originalAllowRemote = TelnetAllowRemote;
        bool originalAuthentication = TelnetAuthenticationEnabled;
        try
        {
            TelnetPort = 23_230;
            TelnetAllowRemote = false;
            TelnetAuthenticationEnabled = false;
            await ToggleTelnetAsync();
            if (!IsTelnetRunning)
            {
                throw new InvalidOperationException("Telnet service failed to start.");
            }

            await ToggleTelnetAsync();
            if (IsTelnetRunning)
            {
                throw new InvalidOperationException("Telnet service failed to stop.");
            }
        }
        finally
        {
            TelnetPort = originalPort;
            TelnetAllowRemote = originalAllowRemote;
            TelnetAuthenticationEnabled = originalAuthentication;
        }
    }

    [RelayCommand]
    private void RefreshBridgePorts()
    {
        BridgePortOptions.Clear();
        if (_mainViewModel is null)
        {
            return;
        }

        foreach (SessionViewModel session in _mainViewModel.Sessions.Where(session => session.IsOpen))
        {
            BridgePortOptions.Add(session.PortName);
        }

        if (BridgePortOptions.Count > 0 && !BridgePortOptions.Contains(BridgePortName))
        {
            BridgePortName = BridgePortOptions[0];
        }
    }

    [RelayCommand]
    private void ToggleBridge()
    {
        if (_telnetBridge.IsBound)
        {
            _telnetBridge.Unbind();
        }
        else
        {
            if (string.IsNullOrWhiteSpace(BridgePortName))
            {
                TelnetBridgeStatus = GetResourceString("Tools.BridgeNoPort");
                return;
            }

            if (!IsTelnetRunning)
            {
                if (TelnetAllowRemote && !TelnetAuthenticationEnabled)
                {
                    TelnetBridgeStatus = GetResourceString("Tools.TelnetRemoteAuthenticationRequired");
                    return;
                }

                try
                {
                    _telnetBridge.ConfigureAuthentication(new TelnetAuthenticationOptions(
                        TelnetAuthenticationEnabled,
                        TelnetUsername.Trim(),
                        TelnetPassword));
                }
                catch (ArgumentException)
                {
                    TelnetBridgeStatus = GetResourceString("Tools.TelnetCredentialsRequired");
                    return;
                }

                _telnetBridge.Start(new TelnetListenOptions(TelnetPort, TelnetAllowRemote, TelnetAuthenticationEnabled));
                UpdateTelnetStatus();
            }

            _telnetBridge.Bind(BridgePortName);
        }

        IsBridgeBound = _telnetBridge.IsBound;
        TelnetBridgeStatus = _telnetBridge.IsBound
            ? GetResourceString("Tools.BridgeBound").Replace("{0}", _telnetBridge.BoundPortName ?? string.Empty, StringComparison.Ordinal)
            : string.Empty;
    }

    public async ValueTask DisposeAsync()
    {
        _monitorTimer.Stop();
        _monitorTimer.Tick -= OnMonitorTick;
        _telnetBridge.StatusChanged -= OnTelnetStatusChanged;
        _telnetBridge.Diagnostic -= OnTelnetDiagnostic;
        _process.Dispose();
        GC.SuppressFinalize(this);
    }

    private void OnMonitorTick(object? sender, EventArgs e)
    {
        UpdateMonitor();
        RefreshMonitorValues();
    }

    partial void OnShowMemoryMonitorChanged(bool value)
    {
        if (_mainViewModel is not null)
        {
            _mainViewModel.ShowMemoryMonitor = value;
        }
    }

    partial void OnMemoryRefreshIntervalChanged(int value)
    {
        int clamped = Math.Clamp(value, 1, 60);
        if (value != clamped)
        {
            MemoryRefreshInterval = clamped;
            return;
        }

        if (_mainViewModel is not null)
        {
            _mainViewModel.MemoryRefreshInterval = clamped;
        }
    }

    partial void OnSystemMemoryRefreshIntervalChanged(int value)
    {
        int clamped = Math.Clamp(value, 1, 60);
        if (value != clamped)
        {
            SystemMemoryRefreshInterval = clamped;
            return;
        }

        if (_mainViewModel is not null)
        {
            _mainViewModel.SystemMemoryRefreshInterval = clamped;
        }
    }

    private void UpdateMonitor()
    {
        DateTimeOffset now = DateTimeOffset.Now;
        _process.Refresh();
        TimeSpan cpu = _process.TotalProcessorTime;
        double elapsed = Math.Max((now - _lastSample).TotalMilliseconds, 1);
        double cpuPercent = (cpu - _lastCpuTime).TotalMilliseconds / elapsed / Environment.ProcessorCount * 100;
        CpuText = $"{cpuPercent:0.0}%";
        MemoryText = $"Working set {_process.WorkingSet64 / 1024d / 1024d:0.0} MB · Private {_process.PrivateMemorySize64 / 1024d / 1024d:0.0} MB";
        RuntimeText = $"GC {GC.GetTotalMemory(false) / 1024d / 1024d:0.0} MB · Gen {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)} · Threads {_process.Threads.Count}";
        if (_mainViewModel is null || !_mainViewModel.PrivateMemoryMonitorEnabled)
        {
            PrivateMemoryMonitorStatus = GetResourceString("Tools.PrivateMemoryMonitor.Disabled")
                .Replace("{0}", (_mainViewModel?.PrivateMemoryThresholdMiB ?? 1024).ToString(CultureInfo.CurrentCulture), StringComparison.Ordinal);
            IsPrivateMemoryWarningVisible = false;
        }
        else
        {
            double currentMiB = _mainViewModel.PrivateMemoryBytes / 1024d / 1024d;
            PrivateMemoryMonitorStatus = GetResourceString(_mainViewModel.IsPrivateMemoryThresholdReached
                    ? "Tools.PrivateMemoryMonitor.Exceeded"
                    : "Tools.PrivateMemoryMonitor.Running")
                .Replace("{0}", currentMiB.ToString("0.0", CultureInfo.CurrentCulture), StringComparison.Ordinal)
                .Replace("{1}", _mainViewModel.PrivateMemoryThresholdMiB.ToString(CultureInfo.CurrentCulture), StringComparison.Ordinal);
            IsPrivateMemoryWarningVisible = _mainViewModel.IsPrivateMemoryThresholdReached;
        }
        _lastCpuTime = cpu;
        _lastSample = now;
    }

    private void OnTelnetStatusChanged(object? sender, EventArgs e) =>
        App.Current.Dispatcher.BeginInvoke(UpdateTelnetStatus);

    private void UpdateTelnetStatus()
    {
        IsTelnetRunning = _telnetBridge.IsRunning;
        TelnetClientCount = _telnetBridge.ClientCount;
        // Always show what the listener is actually bound to, so an accidentally remote
        // listener is visible in the UI.
        TelnetListenAddressText = _telnetBridge.IsRunning
            ? _telnetBridge.LocalEndPoint ?? string.Empty
            : (TelnetAllowRemote ? "0.0.0.0" : "127.0.0.1");
        TelnetClients.Clear();
        foreach (string endpoint in _telnetBridge.ClientEndpoints)
        {
            TelnetClients.Add(endpoint);
        }
    }

    partial void OnTelnetPortChanged(int value)
    {
        if (_mainViewModel is not null)
        {
            _mainViewModel.TelnetPort = Math.Clamp(value, 1, 65_535);
        }

        if (IsTelnetRunning)
        {
            TelnetBridgeStatus = GetResourceString("Tools.TelnetRestartRequired");
        }
    }

    partial void OnTelnetAllowRemoteChanged(bool value)
    {
        if (_mainViewModel is not null)
        {
            _mainViewModel.TelnetAllowRemote = value;
        }

        if (IsTelnetRunning)
        {
            TelnetBridgeStatus = GetResourceString("Tools.TelnetRestartRequired");
        }

        UpdateTelnetStatus();
    }

    partial void OnTelnetAuthenticationEnabledChanged(bool value)
    {
        if (_mainViewModel is not null)
        {
            _mainViewModel.TelnetAuthenticationEnabled = value;
        }

        if (IsTelnetRunning)
        {
            TelnetBridgeStatus = GetResourceString("Tools.TelnetRestartRequired");
        }
    }

    partial void OnTelnetUsernameChanged(string value)
    {
        if (_mainViewModel is not null)
        {
            _mainViewModel.TelnetUsername = value;
        }

        if (IsTelnetRunning)
        {
            TelnetBridgeStatus = GetResourceString("Tools.TelnetRestartRequired");
        }
    }

    private void OnTelnetDiagnostic(string message)
    {
        Program.DiagnosticLog?.Warning(message);
        App.Current.Dispatcher.BeginInvoke(() => TelnetBridgeStatus = message);
    }

    private static string ControlName(int value) => value switch
    {
        0 => "NUL",
        9 => "TAB",
        10 => "LF",
        13 => "CR",
        27 => "ESC",
        32 => "SPACE",
        127 => "DEL",
        _ => $"CTRL-{value}",
    };

    partial void OnShortcutSearchTextChanged(string value) => RefreshShortcutRows();

    private void RefreshShortcutRows()
    {
        string text = ShortcutSearchText.Trim();
        IEnumerable<ShortcutDefinition> source = _shortcutManager.Definitions;
        if (!string.IsNullOrEmpty(text))
        {
            source = source.Where(definition =>
                LocalizedName(definition).Contains(text, StringComparison.OrdinalIgnoreCase) ||
                definition.GestureText.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                definition.DefaultGestureText.Contains(text, StringComparison.OrdinalIgnoreCase));
        }

        FilteredShortcuts.Clear();
        foreach (ShortcutDefinition definition in source)
        {
            FilteredShortcuts.Add(new ShortcutRow(
                LocalizedName(definition),
                definition.GestureText,
                definition.HasConflict,
                LocalizedConflictMessage(definition),
                definition.DefaultGestureText,
                definition));
        }
    }

    private static string LocalizedName(ShortcutDefinition definition) =>
        (Application.Current.TryFindResource(definition.DisplayName) as string) ?? definition.DisplayName;

    private string LocalizedConflictMessage(ShortcutDefinition definition)
    {
        if (!definition.HasConflict || string.IsNullOrEmpty(definition.ConflictMessage))
        {
            return string.Empty;
        }

        const string prefix = "Shortcut.ConflictWith:";
        if (!definition.ConflictMessage.StartsWith(prefix, StringComparison.Ordinal))
        {
            return (Application.Current.TryFindResource(definition.ConflictMessage) as string) ?? definition.ConflictMessage;
        }

        string[] ids = definition.ConflictMessage[prefix.Length..].Split(',', StringSplitOptions.RemoveEmptyEntries);
        string label = Application.Current.TryFindResource("Shortcut.ConflictWith") as string ?? "Conflicts with";
        string resolved = string.Join(", ", ids.Select(id =>
        {
            ShortcutDefinition? other = _shortcutManager.GetDefinition(id);
            return other is not null ? LocalizedName(other) : id;
        }));
        return $"{label}: {resolved}";
    }

    [RelayCommand]
    private void StartEditShortcut(ShortcutRow? row)
    {
        if (row is null)
        {
            return;
        }

        EditingActionName = row.ActionName;
        EditingGestureText = row.GestureText;
        EditingErrorMessage = string.Empty;
        IsEditingShortcut = true;
    }

    [RelayCommand]
    private void SaveEditedShortcut()
    {
        if (!IsEditingShortcut)
        {
            return;
        }

        ShortcutDefinition? definition = _shortcutManager.Definitions.FirstOrDefault(item =>
            LocalizedName(item) == EditingActionName);
        if (definition is null)
        {
            IsEditingShortcut = false;
            return;
        }

        ShortcutKeyGesture? gesture = ShortcutKeyGesture.Parse(EditingGestureText);
        ShortcutConflictResult result = _shortcutManager.SetGesture(definition.ActionId, gesture);
        if (!result.IsValid)
        {
            EditingErrorMessage = (Application.Current.TryFindResource(result.Message) as string) ?? result.Message;
            return;
        }

        _shortcutManager.Save(ShortcutsFilePath);
        IsEditingShortcut = false;
        RefreshShortcutRows();
    }

    [RelayCommand]
    private void CancelEditShortcut() => IsEditingShortcut = false;

    [RelayCommand]
    private void ResetShortcut(ShortcutRow? row)
    {
        if (row?.Definition is null)
        {
            return;
        }

        _shortcutManager.ResetToDefault(row.Definition.ActionId);
        _shortcutManager.Save(ShortcutsFilePath);
        RefreshShortcutRows();
    }

    [RelayCommand]
    private void ResetAllShortcuts()
    {
        _shortcutManager.ResetAllToDefaults();
        _shortcutManager.Save(ShortcutsFilePath);
        RefreshShortcutRows();
    }

    private static string ShortcutsFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DuCom",
        "shortcuts.json");

    public void ApplyCapturedKeys(Key key, ModifierKeys modifiers)
    {
        if (!IsEditingShortcut)
        {
            return;
        }

        if (key == Key.Escape)
        {
            CancelEditShortcut();
            return;
        }

        if (key is Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
        {
            return;
        }

        var gesture = new ShortcutKeyGesture(key.ToString(), MapModifiers(modifiers));
        if (gesture.IsModifierOnly)
        {
            return;
        }

        EditingGestureText = gesture.ToDisplayText();
    }

    private static ShortcutModifiers MapModifiers(ModifierKeys modifiers)
    {
        ShortcutModifiers result = ShortcutModifiers.None;
        if ((modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            result |= ShortcutModifiers.Ctrl;
        }

        if ((modifiers & ModifierKeys.Alt) == ModifierKeys.Alt)
        {
            result |= ShortcutModifiers.Alt;
        }

        if ((modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
        {
            result |= ShortcutModifiers.Shift;
        }

        if ((modifiers & ModifierKeys.Windows) == ModifierKeys.Windows)
        {
            result |= ShortcutModifiers.Win;
        }

        return result;
    }

    public sealed record ShortcutRow(
        string ActionName,
        string GestureText,
        bool HasConflict,
        string ConflictMessage,
        string DefaultGestureText,
        ShortcutDefinition Definition);

    public sealed record AsciiRow(int DecimalValue, string Hex, string Character);

    public sealed record PluginRow(
        string Name,
        string Version,
        string Description,
        string Path,
        bool IsBackgroundPlugin);
}
