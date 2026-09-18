using System.Diagnostics;
using System.Text;
using DuCom.PluginHost.Core;

namespace DuCom.PluginHost;

public sealed partial class PluginSystemService
{
    private long _nextMemorySampleTimestamp;

    private void LogMemorySample(PluginBudgetSample sample, long now)
    {
        Action<string>? log = ProgramLog;
        if (log is null)
        {
            return;
        }

        long next = Volatile.Read(ref _nextMemorySampleTimestamp);
        if (now < next || Interlocked.CompareExchange(
                ref _nextMemorySampleTimestamp, now + 60L * Stopwatch.Frequency, next) != next)
        {
            return;
        }

        // Reserve the interval before querying or calling subscribers, including reentrant calls.
        try
        {
            long workingSet = -1;
            try
            {
                using Process process = Process.GetCurrentProcess();
                workingSet = process.WorkingSet64;
            }
            catch (Exception)
            {
                // Unavailable is distinct from zero; diagnostics must not interrupt governance.
            }

            StringBuilder message = new(FormattableString.Invariant(
                $"Memory sample: unit=bytes hostPid={Environment.ProcessId} hostPrivate={sample.HostPrivateBytes} hostWorkingSet={workingSet} totalPrivate={sample.TotalPrivateBytes} managed={sample.ManagedHeapBytes} heap={sample.ManagedHeapSizeBytes} fragmented={sample.ManagedFragmentedBytes} workers=["));
            bool first = true;
            foreach ((string pluginId, uint pid, long privateBytes) in sample.Workers)
            {
                if (!first) message.Append("; ");
                first = false;
                message.Append(FormattableString.Invariant($"pid={pid} private={privateBytes} plugin={System.Text.Json.JsonSerializer.Serialize(pluginId)}"));
            }
            message.Append(']');
            log(message.ToString());
        }
        catch (Exception)
        {
            // A diagnostic sink failure must not skip recovery or later budget subscribers.
        }
    }
}
