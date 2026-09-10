using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace DuCom.ViewModels;

public partial class ToolCenterViewModel
{
    private readonly Process _process = Process.GetCurrentProcess();
    private readonly DispatcherTimer _monitorTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private TimeSpan _lastCpuTime;
    private DateTimeOffset _lastSample = DateTimeOffset.Now;

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

    public ObservableCollection<WatchdogRuleRow> WatchdogRows { get; } = [];

    public ObservableCollection<MonitorRuleRow> MonitorRows { get; } = [];

    public ObservableCollection<MonitorValueRow> MonitorValues { get; } = [];

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
}
