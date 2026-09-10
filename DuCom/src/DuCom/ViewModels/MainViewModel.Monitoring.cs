using DuCom.Core.Diagnostics;
using DuCom.PluginHost;

namespace DuCom.ViewModels;

public partial class MainViewModel
{
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
        VariableMonitor.UpdateRules([.. Services.VariableMonitorRuleStore.Load()]);
    }

    internal void SaveMonitorRules(VariableMonitorRule[] rules)
    {
        Services.VariableMonitorRuleStore.Save(rules);
        VariableMonitor.UpdateRules(rules);
        Program.DiagnosticLog?.Information($"Saved {rules.Length} monitor rules.");
    }
}
