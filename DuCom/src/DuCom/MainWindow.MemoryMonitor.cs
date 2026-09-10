using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using DuCom.ViewModels;

namespace DuCom;

public partial class MainWindow
{
    private readonly DispatcherTimer _processMemoryTimer = new(DispatcherPriority.Background);
    private readonly DispatcherTimer _systemMemoryTimer = new(DispatcherPriority.Background);

    private void ConfigureMemoryMonitor(MainViewModel viewModel)
    {
        StopMemoryMonitor();
        if (!viewModel.ShowMemoryMonitor)
        {
            ProcessMemoryText.Text = "-- MB";
            ProcessMemoryTooltip.Text = string.Empty;
            SystemMemoryText.Text = "--%";
            SystemMemoryTooltip.Text = string.Empty;
            return;
        }

        _processMemoryTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(viewModel.MemoryRefreshInterval, 1, 60));
        _systemMemoryTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(viewModel.SystemMemoryRefreshInterval, 1, 60));
        UpdateProcessMemory();
        UpdateSystemMemory();
        _processMemoryTimer.Start();
        _systemMemoryTimer.Start();
    }

    private void StopMemoryMonitor()
    {
        _processMemoryTimer.Stop();
        _systemMemoryTimer.Stop();
    }

    private void ProcessMemoryTimer_Tick(object? sender, EventArgs e) => UpdateProcessMemory();

    private void SystemMemoryTimer_Tick(object? sender, EventArgs e) => UpdateSystemMemory();

    private void UpdateProcessMemory()
    {
        try
        {
            using Process process = Process.GetCurrentProcess();
            process.Refresh();
            long privateMiB = (long)Math.Round(process.PrivateMemorySize64 / 1024d / 1024d);
            long workingSetMiB = (long)Math.Round(process.WorkingSet64 / 1024d / 1024d);
            long gcMiB = (long)Math.Round(GC.GetTotalMemory(false) / 1024d / 1024d);
            ProcessMemoryText.Text = $"{privateMiB} MB";
            ProcessMemoryTooltip.Text = FormatResource(
                "MemoryMonitor.ProcessTooltip",
                workingSetMiB.ToString(CultureInfo.CurrentCulture),
                privateMiB.ToString(CultureInfo.CurrentCulture),
                gcMiB.ToString(CultureInfo.CurrentCulture));
        }
        catch (Exception exception)
        {
            Program.DiagnosticLog?.Warning("Failed to sample title-bar process memory.", exception);
        }
    }

    private void UpdateSystemMemory()
    {
        try
        {
            Microsoft.VisualBasic.Devices.ComputerInfo info = new();
            ulong totalBytes = info.TotalPhysicalMemory;
            ulong availableBytes = info.AvailablePhysicalMemory;
            ulong usedBytes = totalBytes > availableBytes ? totalBytes - availableBytes : 0;
            int usedPercent = totalBytes == 0
                ? 0
                : (int)Math.Round(usedBytes * 100d / totalBytes);

            string statusBrushKey = usedPercent switch
            {
                >= 90 => "Brush.Danger",
                >= 60 => "Brush.Caution",
                _ => "Brush.Success",
            };

            Brush statusBrush = Application.Current.TryFindResource(statusBrushKey) as Brush
                ?? Brushes.Gray;
            SystemMemoryDot.Fill = statusBrush;
            SystemMemoryText.Foreground = statusBrush;
            SystemMemoryText.Text = $"{usedPercent}%";
            SystemMemoryTooltip.Text = FormatResource(
                "MemoryMonitor.SystemTooltip",
                (usedBytes / 1024d / 1024d / 1024d).ToString("0.0", CultureInfo.CurrentCulture),
                (totalBytes / 1024d / 1024d / 1024d).ToString("0.0", CultureInfo.CurrentCulture),
                (availableBytes / 1024d / 1024d / 1024d).ToString("0.0", CultureInfo.CurrentCulture));
        }
        catch (Exception exception)
        {
            Program.DiagnosticLog?.Warning("Failed to sample title-bar system memory.", exception);
        }
    }

    private static string FormatResource(string key, params string[] values)
    {
        string result = Application.Current.TryFindResource(key) as string ?? key;
        for (int index = 0; index < values.Length; index++)
        {
            result = result.Replace($"{{{index}}}", values[index], StringComparison.Ordinal);
        }

        return result;
    }
}
