using System.Diagnostics;
using System.IO;
using System.Windows;
using DuCom.Core.Logging;

namespace DuCom.Services;

internal static class SystemLogAccess
{
    private static string LogRoot => Path.Combine(AppContext.BaseDirectory, "Logs");

    public static string DirectoryPath => LogFolderLayout.System(LogRoot, DateTimeOffset.Now);

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

    /// <summary>Startup soft-quota pass; DiagnosticFileLog also enforces it on every real rotation.</summary>
    public static void EnforceQuota(string? activeFilePath)
        => LogFolderQuota.Enforce(
            DirectoryPath,
            LogFolderQuota.DefaultQuotaBytes,
            LogFolderQuota.IsSystemLogFile,
            activeFilePath is null ? null : [activeFilePath]);
}
