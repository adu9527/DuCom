using System.Windows;

namespace DuCom.Services;

/// <summary>
/// Applies a stable device-independent type scale. WPF's PerMonitorV2 manifest scales
/// these DIPs for the monitor containing each window, including after a cross-monitor move.
/// Physical screen width must never introduce a second, global font zoom.
/// </summary>
public static class DensityService
{
    public static double DpiScale { get; private set; } = 1.0;
    public static int PhysicalWidth { get; private set; }
    public static int PhysicalHeight { get; private set; }
    public static string Mode { get; private set; } = "PerMonitor";
    public static double Factor { get; private set; } = 1.0;

    public static void Apply(ResourceDictionary resources)
    {
        ArgumentNullException.ThrowIfNull(resources);
        // Diagnostic values are logical desktop units, not a density-selection heuristic.
        PhysicalWidth = (int)SystemParameters.PrimaryScreenWidth;
        PhysicalHeight = (int)SystemParameters.PrimaryScreenHeight;
        resources["Fs.Xs"] = 12d;
        resources["Fs.Small"] = 13d;
        resources["Fs.Body"] = 14d;
        resources["Fs.Log"] = 13d;
        resources["Fs.Title"] = 20d;
        resources["Fs.Caption"] = 12d;
        resources["Density.Factor"] = 1d;
    }
}
