using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using DuCom.Core.Diagnostics;

namespace DuCom.ViewModels;

public partial class MainViewModel
{
    private Services.PrivateMemoryMonitorService? _privateMemoryMonitor;
    private bool _privateMemoryThresholdWasReached;

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

    [ObservableProperty]
    public partial bool PrivateMemoryMonitorEnabled { get; set; }

    [ObservableProperty]
    public partial int PrivateMemoryThresholdMiB { get; set; } = 1024;

    [ObservableProperty]
    public partial bool ShowMemoryMonitor { get; set; } = true;

    [ObservableProperty]
    public partial int MemoryRefreshInterval { get; set; } = 2;

    [ObservableProperty]
    public partial int SystemMemoryRefreshInterval { get; set; } = 5;

    [ObservableProperty]
    public partial long PrivateMemoryBytes { get; private set; }

    [ObservableProperty]
    public partial bool IsPrivateMemoryThresholdReached { get; private set; }

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
    partial void OnShowMemoryMonitorChanged(bool value) => MarkSettingsDirty();
    partial void OnMemoryRefreshIntervalChanged(int value) => MarkSettingsDirty();
    partial void OnSystemMemoryRefreshIntervalChanged(int value) => MarkSettingsDirty();

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
}
