using System.Diagnostics;
using System.IO;
using System.Windows;

namespace DuCom.Services;

internal static class SystemLogAccess
{
    public static string DirectoryPath => Path.Combine(AppContext.BaseDirectory, "Logs", "System_log");

    public static void OpenCurrent()
    {
        Directory.CreateDirectory(DirectoryPath);
        if (Program.DiagnosticLog is { IsAvailable: false })
        {
            MessageBox.Show(
                Application.Current?.TryFindResource("Feedback.SystemLogUnavailable") as string ?? "The system log could not be created for this run.",
                Application.Current?.TryFindResource("Feedback.SystemLog") as string ?? "System log",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        string? currentLogPath = Program.DiagnosticLog?.FilePath;
        if (!string.IsNullOrWhiteSpace(currentLogPath) && File.Exists(currentLogPath))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{currentLogPath}\"") { UseShellExecute = true });
            return;
        }

        Process.Start(new ProcessStartInfo(DirectoryPath) { UseShellExecute = true });
    }
}
