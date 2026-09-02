using DuCom.Core.Diagnostics;

namespace DuCom.Services;

/// <summary>
/// Per-port variable monitoring on a one-second single-flight background worker, fed by
/// immutable UI-thread session snapshots through <see cref="VariableMonitorEngine"/>. Never
/// runs in receive callbacks, never enumerates WPF collections from the timer thread, and
/// disposal cancels and waits for the in-flight tick.
/// </summary>
public sealed class VariableMonitorService : IDisposable
{
    private readonly SessionProbeProvider _probes;
    private readonly VariableMonitorEngine _engine = new();
    private readonly PeriodicBackgroundWorker _worker;
    private readonly object _disposeGate = new();
    private Task? _disposeTask;

    public VariableMonitorService(SessionProbeProvider probes)
    {
        _probes = probes ?? throw new ArgumentNullException(nameof(probes));
        _worker = new PeriodicBackgroundWorker(
            "variable-monitor",
            TimeSpan.FromSeconds(1),
            (cancellationToken) => Task.Run(() => _engine.Tick(_probes.MonitorSnapshot), cancellationToken),
            (name, exception) => Program.DiagnosticLog?.Error($"{name} tick failed. {exception}"));
        _worker.Start();
    }

    public void UpdateRules(IReadOnlyList<VariableMonitorRule> rules) => _engine.UpdateRules(rules);

    public bool IsEmpty => _engine.IsEmpty;

    public IReadOnlyList<VariableMonitorRule> Rules => _engine.Rules;

    public IReadOnlyList<(VariableMonitorRule Rule, VariableMonitorSample? Sample)> GetRuleStates() => _engine.GetRuleStates();

    /// <summary>Exports one CSV row per rule with its latest sample.</summary>
    public string ExportCsv()
    {
        System.Text.StringBuilder builder = new("Name,Port,Pattern,Enabled,Order,Value,SampledAtUtc,MatchCount\r\n");
        foreach ((VariableMonitorRule rule, VariableMonitorSample? sample) in GetRuleStates())
        {
            builder.Append(Escape(rule.Name)).Append(',')
                .Append(Escape(rule.PortName ?? string.Empty)).Append(',')
                .Append(Escape(rule.Pattern)).Append(',')
                .Append(rule.IsEnabled ? "1" : "0").Append(',')
                .Append(rule.Order).Append(',')
                .Append(Escape(sample?.Value ?? string.Empty)).Append(',')
                .Append(Escape(sample?.SampledAtUtc.ToString("O") ?? string.Empty)).Append(',')
                .Append(sample?.MatchCount ?? 0).Append("\r\n");
        }

        return builder.ToString();
    }

    private static string Escape(string value) =>
        value.Contains(',') || value.Contains('"') || value.Contains('\n')
            ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : value;

    public void Dispose()
    {
        Task disposeTask;
        lock (_disposeGate)
        {
            disposeTask = _disposeTask ??= DisposeCoreAsync();
        }

        try
        {
            disposeTask.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
        }
    }

    private async Task DisposeCoreAsync()
    {
        try
        {
            await _worker.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
        }
    }
}
