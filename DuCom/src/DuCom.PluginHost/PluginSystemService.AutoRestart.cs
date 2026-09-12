using DuCom.PluginHost.Core;
using DuCom.PluginHost.Registry;

namespace DuCom.PluginHost;

/// <summary>
/// Pure crash-restart decision for the plugin auto-restart feature: given the crash
/// streak state and when the plugin last started running healthily, decide whether to
/// schedule another restart, with which exponential backoff delay. Kept free of
/// side effects and timers so the policy itself is unit-testable.
/// </summary>
internal static class AutoRestartPolicy
{
    public const int MaxConsecutiveRestarts = 3;

    public static readonly TimeSpan BaseDelay = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(60);

    /// <summary>
    /// A plugin that kept running for at least this long before a crash counts as
    /// healthy, so the crash begins a fresh streak instead of extending the previous one.
    /// </summary>
    public static readonly TimeSpan StableWindow = TimeSpan.FromMinutes(5);

    public readonly record struct Decision(bool Schedule, int Attempt, TimeSpan Delay, string? SkipReason);

    public static Decision OnFault(int? previousConsecutiveCrashes, DateTimeOffset? lastHealthyStartUtc, DateTimeOffset faultUtc)
    {
        int consecutive;
        if (previousConsecutiveCrashes is null)
        {
            consecutive = 1;
        }
        else
        {
            bool ranHealthySinceLastStart = lastHealthyStartUtc is { } healthyStart
                && faultUtc - healthyStart >= StableWindow;
            consecutive = ranHealthySinceLastStart ? 1 : previousConsecutiveCrashes.Value + 1;
        }

        if (consecutive > MaxConsecutiveRestarts)
        {
            return new Decision(false, consecutive, default,
                $"gave up after {MaxConsecutiveRestarts} consecutive crashes; manual retry required");
        }

        return new Decision(true, consecutive, DelayFor(consecutive), null);
    }

    /// <summary>Exponential backoff for the Nth attempt (1-based), doubling from
    /// <see cref="BaseDelay"/> and capped at <see cref="MaxDelay"/>.</summary>
    public static TimeSpan DelayFor(int attempt)
    {
        TimeSpan delay = BaseDelay;
        for (int index = 1; index < attempt; index++)
        {
            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, MaxDelay.Ticks));
        }

        return delay;
    }
}

public sealed partial class PluginSystemService
{
    private readonly object _autoRestartGate = new();
    private readonly Dictionary<string, int> _autoRestartCrashes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _lastHealthyStartUtc = new(StringComparer.Ordinal);
    private readonly HashSet<string> _userStopRequested = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _autoRestartCancellation = new();

    /// <summary>
    /// Wired in <see cref="GetOrCreateController"/>: reacts to a controller entering
    /// FaultDisabled, which only happens through genuine faults — user stops end in
    /// Disabled and budget stops end in StoppedByBudget, so neither reaches here.
    /// </summary>
    private void OnControllerFaultDisabled(PluginStateChange change)
    {
        if (change.Next is not PluginRuntimeState.FaultDisabled)
        {
            return;
        }

        ScheduleAutoRestart(change.PluginId, change.Reason);
    }

    private void ScheduleAutoRestart(string pluginId, string? reason)
    {
        lock (_autoRestartGate)
        {
            if (_userStopRequested.Remove(pluginId))
            {
                // The stop that produced this fault was requested by the user (its exit
                // confirmation failed); restarting would override their intent.
                ProgramLog?.Invoke($"Auto-restart skipped for '{pluginId}': the fault followed a user-requested stop ({reason}).");
                return;
            }

            if (_autoRestartCancellation.IsCancellationRequested)
            {
                return;
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            int? previousCrashes = _autoRestartCrashes.TryGetValue(pluginId, out int crashes) ? crashes : null;
            DateTimeOffset? healthyStart = _lastHealthyStartUtc.TryGetValue(pluginId, out DateTimeOffset started) ? started : null;
            AutoRestartPolicy.Decision decision = AutoRestartPolicy.OnFault(previousCrashes, healthyStart, now);
            _autoRestartCrashes[pluginId] = decision.Attempt;
            if (!decision.Schedule)
            {
                ProgramLog?.Invoke($"Auto-restart for '{pluginId}': {decision.SkipReason} ({reason}).");
                return;
            }

            ProgramLog?.Invoke($"Auto-restart of '{pluginId}' scheduled in {decision.Delay.TotalSeconds:0.#}s (attempt {decision.Attempt}/{AutoRestartPolicy.MaxConsecutiveRestarts}) after fault: {reason}");
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(decision.Delay, _autoRestartCancellation.Token).ConfigureAwait(false);
                    await RestartAfterFaultAsync(pluginId).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception exception)
                {
                    ProgramLog?.Invoke($"Auto-restart of '{pluginId}' failed: {exception.Message}");
                }
            }, CancellationToken.None);
        }
    }

    private async Task RestartAfterFaultAsync(string pluginId)
    {
        PluginRegistryEntry? entry = _registry.Current.Plugins.TryGetValue(pluginId, out PluginRegistryEntry? found) ? found : null;
        if (entry is null || !entry.Enabled || entry.FaultDisabled is null)
        {
            // The plugin was uninstalled, disabled, or already cleared by the user
            // while the restart was pending; either way the fault state is no longer ours.
            return;
        }

        ClearFaultDisable(pluginId);
        if (await StartRegisteredAsync(pluginId).ConfigureAwait(false))
        {
            ProgramLog?.Invoke($"Auto-restart of '{pluginId}' succeeded.");
        }
        else
        {
            ProgramLog?.Invoke($"Auto-restart of '{pluginId}' did not start the plugin; it stays fault-disabled for manual retry.");
        }
    }

    /// <summary>
    /// Called when any start completes successfully (auto-restart or user action):
    /// records when the plugin began running so a later crash can be judged against
    /// the stable window. The crash streak itself is not cleared here — that only
    /// happens once the plugin has actually stayed up for the stable window.
    /// </summary>
    private void NotifyPluginStarted(string pluginId)
    {
        lock (_autoRestartGate)
        {
            _userStopRequested.Remove(pluginId);
            _lastHealthyStartUtc[pluginId] = DateTimeOffset.UtcNow;
        }
    }

    private void MarkUserStopRequested(string pluginId)
    {
        lock (_autoRestartGate)
        {
            _userStopRequested.Add(pluginId);
        }
    }

    private void CancelAutoRestarts()
    {
        lock (_autoRestartGate)
        {
            _autoRestartCancellation.Cancel();
            _autoRestartCrashes.Clear();
            _lastHealthyStartUtc.Clear();
            _userStopRequested.Clear();
        }
    }
}
