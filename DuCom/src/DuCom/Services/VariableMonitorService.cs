using DuCom.Core.Diagnostics;

namespace DuCom.Services;

/// <summary>Single-flight variable ingestion loop with immutable plot snapshots.</summary>
public sealed class VariableMonitorService : IDisposable
{
    private readonly SessionProbeProvider _probes;
    private readonly VariableMonitorEngine _engine;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _loopTask;
    private int _ingestionIntervalMs;
    private int _disposed;

    public VariableMonitorService(SessionProbeProvider probes, VariablePlotSettings? settings = null)
    {
        _probes = probes ?? throw new ArgumentNullException(nameof(probes));
        settings ??= new VariablePlotSettings();
        _ingestionIntervalMs = Math.Clamp(settings.IngestionIntervalMs, 50, 1_000);
        _engine = new VariableMonitorEngine(new VariableMonitorEngineOptions(
            Math.Clamp(settings.MaximumRawPointsPerSeries, 100, 1_000_000),
            TimeSpan.FromSeconds(Math.Clamp(settings.RetentionSeconds, 1, 86_400))));
        _loopTask = Task.Run(RunAsync);
    }

    public int IngestionIntervalMs
    {
        get => Volatile.Read(ref _ingestionIntervalMs);
        set => Volatile.Write(ref _ingestionIntervalMs, Math.Clamp(value, 50, 1_000));
    }

    public void UpdateRules(IReadOnlyList<VariableMonitorRule> rules) => _engine.UpdateRules(rules);
    public bool IsEmpty => _engine.IsEmpty;
    public IReadOnlyList<VariableMonitorRule> Rules => _engine.Rules;
    public IReadOnlyList<(VariableMonitorRule Rule, VariableMonitorSample? Sample)> GetRuleStates() => _engine.GetRuleStates();
    public VariablePlotSnapshot GetPlotSnapshot() => _engine.GetPlotSnapshot();
    public void ClearPlot() => _engine.ClearHistory();
    public string ExportPlotCsv() => VariablePlotCsv.ToLongTable(GetPlotSnapshot());

    /// <summary>Exports one CSV row per rule with its latest sample.</summary>
    public string ExportCsv()
    {
        System.Text.StringBuilder builder = new("Name,Port,Pattern,Enabled,Order,Value,SampledAtUtc,MatchCount\r\n");
        foreach ((VariableMonitorRule rule, VariableMonitorSample? sample) in GetRuleStates())
        {
            builder.Append(Escape(rule.Name)).Append(',').Append(Escape(rule.PortName ?? string.Empty)).Append(',')
                .Append(Escape(rule.Pattern)).Append(',').Append(rule.IsEnabled ? "1" : "0").Append(',')
                .Append(rule.Order).Append(',').Append(Escape(sample?.Value ?? string.Empty)).Append(',')
                .Append(Escape(sample?.SampledAtUtc.ToString("O") ?? string.Empty)).Append(',')
                .Append(sample?.MatchCount ?? 0).Append("\r\n");
        }

        return builder.ToString();
    }

    private async Task RunAsync()
    {
        try
        {
            while (!_cancellation.IsCancellationRequested)
            {
                await Task.Delay(IngestionIntervalMs, _cancellation.Token).ConfigureAwait(false);
                _engine.Tick(_probes.MonitorSnapshot);
            }
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Program.DiagnosticLog?.Error($"variable-monitor loop failed. {exception}");
        }
    }

    private static string Escape(string value) => value.Contains(',') || value.Contains('"') || value.Contains('\n')
        ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
        : value;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _cancellation.Cancel();
        try { _loopTask.Wait(TimeSpan.FromSeconds(5)); } catch (AggregateException) { }
        _cancellation.Dispose();
    }
}
