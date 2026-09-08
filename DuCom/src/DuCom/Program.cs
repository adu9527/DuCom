using System.IO;
using System.Runtime.InteropServices;
using DuCom.Core.Diagnostics;
using DuCom.Services;
using DuCom.Services.Plugins;
using Velopack;

namespace DuCom;

public static class Program
{
    private const string SingleInstanceMutexName = @"Local\DuCom.Application.SingleInstance";

    private static DiagnosticFileLog? _log;
    private static Mutex? _singleInstanceMutex;

    internal static DiagnosticFileLog? DiagnosticLog => _log;

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--plugin-worker", StringComparer.OrdinalIgnoreCase))
        {
            return PluginWorker.PluginWorkerMain.Run();
        }

        if (TryParseWaitParent(args, out int parentPid))
        {
            WaitForParentExit(parentPid, TimeSpan.FromSeconds(30));
        }

        VelopackApp.Build().Run();
        if (!TryAcquireSingleInstanceLock())
        {
            return 0;
        }

        string logDirectory = SystemLogAccess.DirectoryPath;
        DiagnosticFileLog.PruneDirectory(logDirectory);
        string logFileName = $"ducom-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.log";
        _log = new DiagnosticFileLog(logDirectory, logFileName);

        try
        {
            _log.Information($"Process starting. ApplicationVersion={PluginHostVersion.ApplicationVersion}; PluginCompatibilityVersion={PluginHostVersion.CompatibilityVersion}; Runtime={RuntimeInformation.FrameworkDescription}; OS={RuntimeInformation.OSDescription}; BaseDirectory={AppContext.BaseDirectory}");
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
            ReleaseSingleInstanceLock();
        }
    }

    /// <summary>
    /// A second instance cannot coexist with the first: the portable self-update swap
    /// would fail while another process still has the executable loaded. Fail-open when
    /// the mutex itself misbehaves so the mutex can never brick startup.
    /// </summary>
    private static bool TryAcquireSingleInstanceLock()
    {
        try
        {
            _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool createdNew);
            if (createdNew)
            {
                return true;
            }

            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            System.Windows.MessageBox.Show(
                "DuCom 已在运行。\nDuCom is already running.",
                "DuCom",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
            return false;
        }
        catch
        {
            return true;
        }
    }

    private static void ReleaseSingleInstanceLock()
    {
        try
        {
            _singleInstanceMutex?.ReleaseMutex();
        }
        catch
        {
        }

        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;
    }

    private static bool TryParseWaitParent(string[] args, out int parentPid)
    {
        parentPid = 0;
        for (int index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], "--wait-parent", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(args[index + 1], out int pid))
            {
                parentPid = pid;
                return true;
            }
        }

        return false;
    }

    private static void WaitForParentExit(int parentPid, TimeSpan timeout)
    {
        try
        {
            using System.Diagnostics.Process parent = System.Diagnostics.Process.GetProcessById(parentPid);
            parent.WaitForExit((int)timeout.TotalMilliseconds);
        }
        catch (Exception)
        {
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
