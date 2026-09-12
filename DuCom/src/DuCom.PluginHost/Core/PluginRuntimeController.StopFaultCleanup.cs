using DuCom.Plugin;
using DuCom.PluginHost.Diagnostics;
using DuCom.PluginHost.Registry;
using DuCom.PluginHost.Security;
using DuCom.PluginHost.Transport;

namespace DuCom.PluginHost.Core;

public sealed partial class PluginRuntimeController
{
    public async Task StopAsync()
    {
        if (_state is not (PluginRuntimeState.Active or PluginRuntimeState.Activating or PluginRuntimeState.Starting))
        {
            return;
        }

        Transition(PluginRuntimeState.Stopping, "user stop");
        RevokeImmediately("stop");

        try
        {
            await SendRequestAsync(PluginOps.PluginDeactivate, null, TimeSpan.FromMilliseconds(_limits.StopGraceMs)).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Deactivation is best effort on stop; the worker is terminated regardless.
            PluginHostTrace.Info($"Deactivate request to '{_manifest.Id}' failed during stop: {exception.Message}");
        }

        bool exited = await TerminateWorkerAsync().ConfigureAwait(false);
        if (!exited)
        {
            await FaultAsync("正常停止后未确认 worker 退出 / worker exit was not confirmed after stop", exitConfirmed: false).ConfigureAwait(false);
            return;
        }

        MarkAttemptEnded(clean: true);
        Transition(PluginRuntimeState.Disabled, "user stop");
    }

    public async Task StopForBudgetAsync(PluginBudgetSample sample)
    {
        if (_state is not (PluginRuntimeState.Active or PluginRuntimeState.Activating))
        {
            return;
        }

        Transition(PluginRuntimeState.Stopping, "budget protection");
        RevokeImmediately("budget");
        await TerminateWorkerAsync().ConfigureAwait(false);
        Transition(PluginRuntimeState.StoppedByBudget, "total budget protection");
        _diagnostics.Write(PluginLogLevel.Warning, $"Stopped to protect the tool-wide memory budget. Host={sample.HostPrivateBytes} Total={sample.TotalPrivateBytes}");
        FaultNotice?.Invoke(new HostFaultNotice
        {
            PluginId = _manifest.Id,
            PluginName = _manifest.Name,
            Version = _manifest.Version,
            Reason = "total-budget-protection",
            ActivationId = _activationId,
            ExitConfirmed = true,
            BudgetProtective = true,
        });
    }

    public async Task FaultAsync(string reason, bool exitConfirmed)
    {
        if (Interlocked.Exchange(ref _faultHandling, 1) == 1)
        {
            return;
        }

        try
        {
            if (_state is PluginRuntimeState.FaultDisabled)
            {
                return;
            }

            Transition(PluginRuntimeState.Stopping, reason);
            RevokeImmediately(reason);
            bool terminated = await TerminateWorkerAsync();
            exitConfirmed &= terminated;
            MarkAttemptEnded(clean: false);

            _registry.Mutate(data =>
            {
                PluginRegistryEntry entry = EnsureEntry(data);
                data.Plugins[_manifest.Id] = entry with
                {
                    FaultDisabled = new FaultDisableRecord
                    {
                        Version = _manifest.Version,
                        Digest = _digest,
                        Reason = reason,
                        ActivationId = _activationId,
                        FaultAtUtc = DateTime.UtcNow,
                        Notified = false,
                        ExitConfirmed = exitConfirmed,
                    },
                };
                if (!data.PendingNotices.Any(notice => notice.PluginId == _manifest.Id && notice.ActivationId == _activationId))
                {
                    data.PendingNotices.Add(new PendingNoticeRecord
                    {
                        PluginId = _manifest.Id,
                        Version = _manifest.Version,
                        Reason = reason,
                        ActivationId = _activationId,
                        ExitConfirmed = exitConfirmed,
                        Kind = NoticeKinds.Fault,
                    });
                }

                return data;
            });

            Transition(PluginRuntimeState.FaultDisabled, reason);
            _diagnostics.Write(PluginLogLevel.Error, $"Fault disabled: {reason} (exitConfirmed={exitConfirmed})");
            if (!_faultNotified)
            {
                _faultNotified = true;
                FaultNotice?.Invoke(new HostFaultNotice
                {
                    PluginId = _manifest.Id,
                    PluginName = _manifest.Name,
                    Version = _manifest.Version,
                    Reason = reason,
                    ActivationId = _activationId,
                    ExitConfirmed = exitConfirmed,
                    BudgetProtective = false,
                });
            }
        }
        finally
        {
            Interlocked.Exchange(ref _faultHandling, 0);
        }
    }

    private void RevokeImmediately(string reason)
    {
        _watchdog?.Dispose();
        _watchdog = null;
        if (_rawTap is not null)
        {
            _rawTap.Dispose();
            _rawTap = null;
        }

        _environment.SessionClosed -= OnEnvironmentSessionClosed;
        lock (_gate)
        {
            _serialSubscriptions.Clear();
        }

        _broker?.Dispose();
        _scope?.Revoke();
        _activationCancellation?.Cancel();
        if (_published is not null)
        {
            _environment.RemoveActivation(_manifest.Id);
            _published = null;
        }

        lock (_gate)
        {
            foreach (TaskCompletionSource<PluginWireMessage> pending in _pendingHostRequests.Values)
            {
                pending.TrySetException(new InvalidOperationException($"The activation stopped: {reason}."));
            }

            _pendingHostRequests.Clear();
            _workerRequestIds.Clear();
        }
    }

    private async Task<bool> TerminateWorkerAsync()
    {
        bool exited = true;
        if (_worker is { } worker)
        {
            _pipe?.Close();
            try
            {
                WindowsInterop.TerminateJobObject(worker.JobHandle, 1);
            }
            catch (Exception exception)
            {
                PluginHostTrace.Warning($"Job termination failed for '{_manifest.Id}' (pid {worker.ProcessId}); falling back to the 5s wait.", exception);
            }

            int waitResult = WindowsInterop.WaitForSingleObject(worker.ProcessHandle, 5000);
            exited = waitResult == 0;
            _budget.UnregisterWorker(worker.ProcessId);
            WindowsInterop.CloseHandle(worker.ProcessHandle);
            WindowsInterop.CloseHandle(worker.JobHandle);
            _worker = null;
        }

        if (_pipe is not null)
        {
            await _pipe.DisposeAsync().ConfigureAwait(false);
            _pipe = null;
        }

        CleanupActivationTemp();
        return exited;
    }

    private void CleanupActivationTemp()
    {
        if (_activationId.Length == 0)
        {
            return;
        }

        try
        {
            foreach (string directory in new[]
            {
                Path.Combine(_paths.TempRoot, "WorkerScratch", _manifest.Id, _activationId),
                Path.Combine(_paths.TempRoot, "HostOutput", _manifest.Id, _activationId),
                Path.Combine(_paths.TempRoot, "HostSnapshots", _manifest.Id, _activationId),
            })
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch (Exception exception)
        {
            PluginHostTrace.Warning($"Activation temp cleanup failed for '{_manifest.Id}' (activation {_activationId}).", exception);
        }
    }

    private void MarkAttemptEnded(bool clean)
    {
        string activationId = _activationId;
        _registry.Mutate(data =>
        {
            if (data.Plugins.TryGetValue(_manifest.Id, out PluginRegistryEntry? entry)
                && entry.LastAttempt is { } attempt
                && string.Equals(attempt.ActivationId, activationId, StringComparison.Ordinal))
            {
                data.Plugins[_manifest.Id] = entry with
                {
                    LastAttempt = attempt with { EndedCleanly = clean, EndedUtc = DateTime.UtcNow },
                };
            }

            return data;
        });
    }

    private void OnConnectionClosed(Exception? failure)
    {
        if (_state is PluginRuntimeState.Active or PluginRuntimeState.Activating or PluginRuntimeState.Starting)
        {
            string reason = failure switch
            {
                PipeRateViolationException => $"IPC 洪泛 / IPC flooding: {failure.Message}",
                PipeProtocolException => $"协议违例 / Protocol violation: {failure.Message}",
                _ => "进程退出或 IPC 断开 / process exit or IPC loss",
            };
            _ = FaultAsync(reason, exitConfirmed: false);
        }
    }

    private void WatchdogTick()
    {
        try
        {
            if (_state is not (PluginRuntimeState.Active or PluginRuntimeState.Activating))
            {
                return;
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            if ((now - _lastHeartbeatUtc).TotalMilliseconds > _limits.HeartbeatIntervalMs * _limits.HeartbeatMissLimit)
            {
                _ = FaultAsync($"无响应（连续 {_limits.HeartbeatMissLimit} 次心跳缺失）/ unresponsive", exitConfirmed: false);
                return;
            }

            long windowStart = now.ToUnixTimeMilliseconds() - _limits.SerialSustainedDropWindowMs;
            List<SerialSubscriptionState> laggards = [];
            lock (_gate)
            {
                foreach (SerialSubscriptionState subscription in _serialSubscriptions.Values)
                {
                    while (subscription.Window.Count > 0 && subscription.Window.Peek().Timestamp < windowStart)
                    {
                        subscription.Window.Dequeue();
                    }

                    if (subscription.Window.Count >= 20)
                    {
                        long delivered = subscription.Window.Count(entry => entry.Delivered);
                        if (delivered / (double)subscription.Window.Count < 1d - _limits.SerialSustainedDropThreshold)
                        {
                            laggards.Add(subscription);
                        }
                    }
                }
            }

            if (laggards.Count > 0)
            {
                string detail = laggards.Count == 1 ? $"subscription {laggards[0].SubscriptionId}" : $"{laggards.Count} subscriptions";
                _ = FaultAsync($"持续落后（{detail} 超过丢弃阈值）/ sustained receive backlog", exitConfirmed: false);
            }
        }
        catch (Exception exception)
        {
            // The watchdog must never die, but a silently failing tick would hide why
            // heartbeats or drop detection stopped working.
            _diagnostics.Write(PluginLogLevel.Error, $"Watchdog tick failed: {exception.Message}");
            PluginHostTrace.Error($"Plugin watchdog tick failed for '{_manifest.Id}'.", exception);
        }
    }
}
