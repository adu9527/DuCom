using System.IO;
using System.Runtime.InteropServices;
using DuCom.Core.Diagnostics;

namespace DuCom;

public static class Program
{
    private static DiagnosticFileLog? _log;

    internal static DiagnosticFileLog? DiagnosticLog => _log;

    [STAThread]
    public static int Main(string[] args)
    {
        string logDirectory = Path.Combine(AppContext.BaseDirectory, "Logs", "System_log");
        string logFileName = $"ducom-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.log";
        _log = new DiagnosticFileLog(logDirectory, logFileName);

        try
        {
            _log.Information($"Process starting. Version={typeof(Program).Assembly.GetName().Version}; Runtime={RuntimeInformation.FrameworkDescription}; OS={RuntimeInformation.OSDescription}; BaseDirectory={AppContext.BaseDirectory}");
            App app = new();
            app.InitializeComponent();
            app.DiagnosticLog = _log;
            int exitCode = app.Run();
            _log.Information($"Process exited normally. ExitCode={exitCode}");
            return exitCode;
        }
        catch (Exception exception)
        {
            _log.Error("Fatal exception before or during application startup.", exception);
            ShowStartupFailure(_log.FilePath);
            return -1;
        }
        finally
        {
            _log.Dispose();
            _log = null;
        }
    }

    private static void ShowStartupFailure(string logPath)
    {
        try
        {
            System.Windows.MessageBox.Show(
                $"DuCom 启动失败。\nDuCom failed to start.\n\n诊断日志 / Diagnostic log:\n{logPath}",
                "DuCom 启动错误 / Startup error",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
        catch
        {
        }
    }
}
