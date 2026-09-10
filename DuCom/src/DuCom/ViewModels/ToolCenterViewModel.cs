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
    private readonly TelnetBridgeService _telnetBridge;
    private readonly ShortcutManager _shortcutManager;
    private readonly MainViewModel? _mainViewModel;

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
            ShowMemoryMonitor = _mainViewModel.ShowMemoryMonitor;
            MemoryRefreshInterval = _mainViewModel.MemoryRefreshInterval;
            SystemMemoryRefreshInterval = _mainViewModel.SystemMemoryRefreshInterval;
        }

        RefreshShortcutRows();
        AsciiRows = Enumerable.Range(0, 128)
            .Select(value => new AsciiRow(value, $"0x{value:X2}", value is < 32 or 127 ? ControlName(value) : ((char)value).ToString()))
            .ToArray();
        _monitorTimer.Tick += OnMonitorTick;
        _monitorTimer.Start();
        _telnetBridge.StatusChanged += OnTelnetStatusChanged;
        _telnetBridge.Diagnostic += OnTelnetDiagnostic;
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

    private static string GetResourceString(string key) =>
        Application.Current?.TryFindResource(key) as string ?? key;

    public IReadOnlyList<AsciiRow> AsciiRows { get; }


    [ObservableProperty]
    public partial int SelectedTabIndex { get; set; }


    [RelayCommand]
    private static void OpenDocumentation()
    {
        string path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "docs"));
        if (Directory.Exists(path))
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
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

    public sealed record AsciiRow(int DecimalValue, string Hex, string Character);
}
