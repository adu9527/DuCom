using DuCom.Core.Diagnostics;
using DuCom.PluginHost;

namespace DuCom.ViewModels;

public partial class MainViewModel
{
    private Services.VariableMonitorConfiguration _monitorConfiguration = Services.VariableMonitorConfiguration.Empty;
    private void LoadWatchdogRules()
    {
        WatchdogRules.Clear();
        foreach (WatchdogRule rule in Services.WatchdogRuleStore.Load())
        {
            WatchdogRules.Add(rule);
        }

        Watchdog.UpdateRules([.. WatchdogRules]);
        Program.DiagnosticLog?.Information($"Loaded {WatchdogRules.Count} watchdog rules.");
    }

    internal void SaveWatchdogRules(WatchdogRule[] rules)
    {
        WatchdogRules.Clear();
        foreach (WatchdogRule rule in rules)
        {
            WatchdogRules.Add(rule);
        }

        Services.WatchdogRuleStore.Save([.. WatchdogRules]);
        Watchdog.UpdateRules([.. WatchdogRules]);
        Program.DiagnosticLog?.Information($"Saved {WatchdogRules.Count} watchdog rules.");
    }

    private void LoadMonitorRules()
    {
        ApplyMonitorConfiguration(Services.VariableMonitorRuleStore.LoadConfiguration());
    }

    internal void SaveMonitorRules(VariableMonitorRule[] rules)
    {
        _monitorConfiguration = _monitorConfiguration with { SchemaVersion = 2, Rules = rules, Diagnostics = [] };
        Services.VariableMonitorRuleStore.Save(_monitorConfiguration);
        ApplyMonitorConfiguration(_monitorConfiguration);
        Program.DiagnosticLog?.Information($"Saved {rules.Length} monitor rules.");
    }

    internal void ApplyMonitorConfiguration(Services.VariableMonitorConfiguration configuration)
    {
        _monitorConfiguration = configuration;
        VariableMonitor.IngestionIntervalMs = configuration.Plot.IngestionIntervalMs;
        VariableMonitor.UpdateRules(configuration.Rules);
        if (configuration.Diagnostics.Count > 0)
        {
            Program.DiagnosticLog?.Warning($"Monitor configuration loaded with {configuration.Diagnostics.Count} diagnostic(s): {string.Join("; ", configuration.Diagnostics)}");
        }
    }
}
