namespace DuCom.PluginHost.Diagnostics;

/// <summary>
/// Process-wide trace sink for PluginHost infrastructure failures that have no per-plugin
/// diagnostics instance (activation cleanup, helper task files, temp ledger, host watchdog).
/// The host application wires <see cref="Sink"/> to its diagnostic log at startup; unwired,
/// every call is a no-op so the library stays usable without a host. Never throws.
/// </summary>
public static class PluginHostTrace
{
    public static Action<PluginLogLevel, string, Exception?>? Sink { get; set; }

    public static void Info(string message) => Write(PluginLogLevel.Info, message, null);

    public static void Warning(string message, Exception? exception = null) => Write(PluginLogLevel.Warning, message, exception);

    public static void Error(string message, Exception? exception = null) => Write(PluginLogLevel.Error, message, exception);

    private static void Write(PluginLogLevel level, string message, Exception? exception)
    {
        try
        {
            Sink?.Invoke(level, message, exception);
        }
        catch
        {
            // Tracing must never throw into cleanup paths.
        }
    }
}
