using System.Windows.Threading;
using DuCom.Core.Diagnostics;
using DuCom.ViewModels;

namespace DuCom.Services;

/// <summary>
/// Per-port watchdog evaluated on a one-second single-flight background worker. Lines come
/// from immutable UI-thread session snapshots through <see cref="WatchdogEngine"/> (cursor-
/// based incremental pulls), so the watchdog never enumerates a WPF collection, reads a
/// mutable ViewModel property, or touches receive callbacks. Actions (hint posts, command
/// sends) run inside the awaited tick and use the service cancellation token; disposal
/// cancels and waits for the in-flight tick including its actions.
/// </summary>
public sealed class WatchdogService : IDisposable
{
    private readonly SessionProbeProvider _probes;
    private readonly WatchdogEngine _engine = new();
    private readonly PeriodicBackgroundWorker _worker;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly object _disposeGate = new();
    private Task? _disposeTask;

    public WatchdogService(SessionProbeProvider probes)
    {
        _probes = probes ?? throw new ArgumentNullException(nameof(probes));
        _worker = new PeriodicBackgroundWorker(
            "watchdog",
            TimeSpan.FromSeconds(1),
            TickAsync,
            (name, exception) => Program.DiagnosticLog?.Error($"{name} tick failed. {exception}"));
        _worker.Start();
    }

    public int ActiveContextCount => _engine.ActiveContextCount;

    public bool HasRegexTimedOut => _engine.HasRegexTimedOut;

    public void UpdateRules(IReadOnlyList<WatchdogRule> rules) => _engine.UpdateRules(rules);

    private async Task TickAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<WatchdogTrigger> triggers = _engine.Tick(_probes.WatchdogSnapshot, DateTimeOffset.UtcNow);
        foreach (WatchdogTrigger trigger in triggers)
        {
            await ExecuteActionAsync(trigger, cancellationToken);
        }
    }

    private async Task ExecuteActionAsync(WatchdogTrigger trigger, CancellationToken cancellationToken)
    {
        string reason = $"{trigger.Fired.Rule.Name}: {trigger.Fired.Reason}";
        Program.DiagnosticLog?.Warning($"Watchdog fired. Port={trigger.PortName}; {reason}");
        switch (trigger.Fired.Rule.ActionKind)
        {
            case WatchdogActionKind.DiagnosticLog:
                return;
            case WatchdogActionKind.SendCommand:
                await SendCommandAsync(trigger, cancellationToken);
                return;
            default:
                PostHint(trigger.PortName, GetResourceString("Watchdog.Fired")
                    .Replace("{0}", trigger.Fired.Rule.Name, StringComparison.Ordinal)
                    .Replace("{1}", trigger.Fired.Reason, StringComparison.Ordinal));
                return;
        }
    }

    private async Task SendCommandAsync(WatchdogTrigger trigger, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(trigger.Fired.Rule.ActionCommand))
        {
            Program.DiagnosticLog?.Warning($"Watchdog send action has empty command. Rule={trigger.Fired.Rule.Name}");
            return;
        }

        Core.Telnet.TelnetSessionProbe? probe = _probes.FindTelnetProbe(trigger.PortName);
        if (probe is null)
        {
            // The session closed between evaluation and the action: skip the send instead
            // of throwing into the tick loop.
            Program.DiagnosticLog?.Warning($"Watchdog send skipped, session closed. Port={trigger.PortName}");
            return;
        }

        try
        {
            await probe.SendAsync(trigger.Fired.Rule.ActionCommand, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            Program.DiagnosticLog?.Warning($"Watchdog send failed. Rule={trigger.Fired.Rule.Name}; {exception.Message}");
            PostHint(trigger.PortName, GetResourceString("Watchdog.SendFailed")
                .Replace("{0}", trigger.Fired.Rule.Name, StringComparison.Ordinal));
        }
    }

    private void PostHint(string portName, string hint)
    {
        // Never post after the Dispatcher began shutting down.
        Dispatcher? dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return;
        }

        try
        {
            dispatcher.BeginInvoke(() =>
            {
                // Inside the dispatcher callback we are on the UI thread, where reading the
                // session collection and its properties is safe.
                SessionViewModel? session = _probes.FindViewModel(portName);
                session?.RaiseWatchdogHint(hint);
            });
        }
        catch (OperationCanceledException)
        {
            // Dispatcher shut down between the check and the post.
        }
    }

    private static string GetResourceString(string key) =>
        System.Windows.Application.Current?.TryFindResource(key) as string ?? key;

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
        _cancellation.Cancel();
        try
        {
            await _worker.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
        }

        _cancellation.Dispose();
    }
}
