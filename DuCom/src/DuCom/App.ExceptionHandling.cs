using System.Diagnostics;
using System.Windows.Threading;

namespace DuCom;

public partial class App
{
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Trace.TraceError(e.Exception.ToString());
        DiagnosticLog?.Error("Unhandled Dispatcher exception.", e.Exception);
        e.Handled = true;
        ThemedMessageDialog.Show(
            MainWindow,
            (string)FindResource("Error.UnhandledMessage"),
            (string)FindResource("Error.UnhandledTitle"),
            ThemedMessageDialogKind.Error);
        // Keep the dispatcher pumping while MainWindow asynchronously stops plugins
        // and serial sessions. Synchronous OnExit cleanup can otherwise deadlock on UI work.
        Program.BeginShutdown();
        if (MainWindow is DuCom.MainWindow window)
        {
            window.RequestExit();
        }
        else
        {
            Shutdown(-1);
        }
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        Trace.TraceError(e.ExceptionObject?.ToString());
        DiagnosticLog?.Error(
            $"Unhandled AppDomain exception. IsTerminating={e.IsTerminating}",
            e.ExceptionObject as Exception);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Trace.TraceError(e.Exception.ToString());
        DiagnosticLog?.Error("Unobserved Task exception.", e.Exception);
        e.SetObserved();
    }
}
