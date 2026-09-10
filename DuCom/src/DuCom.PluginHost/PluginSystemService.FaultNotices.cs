using DuCom.PluginHost.Core;
using DuCom.PluginHost.Registry;

namespace DuCom.PluginHost;

public sealed partial class PluginSystemService
{
    private async Task HandleFaultNoticeAsync(HostFaultNotice notice)
    {
        if (_registry.Current.Plugins.TryGetValue(notice.PluginId, out PluginRegistryEntry? builtInEntry) && builtInEntry.BuiltIn)
        {
            _registry.Mutate(data =>
            {
                data.PendingNotices.RemoveAll(pending => pending.PluginId == notice.PluginId);
                if (data.Plugins.TryGetValue(notice.PluginId, out PluginRegistryEntry? entry))
                {
                    data.Plugins[notice.PluginId] = entry with
                    {
                        FaultDisabled = entry.FaultDisabled is null ? null : entry.FaultDisabled with { Notified = true },
                        LastAttempt = entry.LastAttempt is null ? null : entry.LastAttempt with { RecoveryNotified = true },
                    };
                }
                return data;
            });
            ProgramLog?.Invoke($"Built-in plugin '{notice.PluginId}' fault recorded without a user popup: {notice.Reason}");
            FaultNotice?.Invoke(notice);
            Changed?.Invoke();
            return;
        }

        // Persist first so a UI failure, unavailable dispatcher, or host interruption leaves a
        // recoverable notice rather than a permanently suppressed fault warning.
        _registry.Mutate(data =>
        {
            if (!data.PendingNotices.Any(pending => pending.PluginId == notice.PluginId && pending.ActivationId == notice.ActivationId))
            {
                data.PendingNotices.Add(new PendingNoticeRecord
                {
                    PluginId = notice.PluginId,
                    Version = notice.Version,
                    Reason = notice.Reason,
                    ActivationId = notice.ActivationId,
                    ExitConfirmed = notice.ExitConfirmed,
                    Kind = notice.BudgetProtective ? "budget" : NoticeKinds.Fault,
                });
            }
            return data;
        });
        bool shown;
        try
        {
            shown = await _environment.ShowFaultNoticeAsync(notice).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            ProgramLog?.Invoke($"Fault notice for '{notice.PluginId}' could not be displayed: {exception.Message}");
            shown = false;
        }
        if (shown)
        {
            _registry.Mutate(data =>
            {
                data.PendingNotices.RemoveAll(pending => pending.PluginId == notice.PluginId && pending.ActivationId == notice.ActivationId);
                if (data.Plugins.TryGetValue(notice.PluginId, out PluginRegistryEntry? entry)
                    && entry.FaultDisabled?.ActivationId == notice.ActivationId)
                {
                    data.Plugins[notice.PluginId] = entry with
                    {
                        FaultDisabled = entry.FaultDisabled with { Notified = true },
                        LastAttempt = entry.LastAttempt?.ActivationId == notice.ActivationId
                            ? entry.LastAttempt with { RecoveryNotified = true } : entry.LastAttempt,
                    };
                }
                return data;
            });
        }

        FaultNotice?.Invoke(notice);
        Changed?.Invoke();
    }

    private async Task ConsumePendingNoticesAsync()
    {
        _registry.Mutate(data =>
        {
            PendingNoticeRecord[] builtInNotices = [.. data.PendingNotices.Where(notice => data.Plugins.TryGetValue(notice.PluginId, out PluginRegistryEntry? entry) && entry.BuiltIn)];
            data.PendingNotices.RemoveAll(notice => builtInNotices.Any(candidate => candidate.PluginId == notice.PluginId && candidate.ActivationId == notice.ActivationId));
            foreach (PendingNoticeRecord notice in builtInNotices)
            {
                PluginRegistryEntry entry = data.Plugins[notice.PluginId];
                data.Plugins[notice.PluginId] = entry with
                {
                    FaultDisabled = entry.FaultDisabled?.ActivationId == notice.ActivationId ? entry.FaultDisabled with { Notified = true } : entry.FaultDisabled,
                    LastAttempt = entry.LastAttempt?.ActivationId == notice.ActivationId ? entry.LastAttempt with { RecoveryNotified = true } : entry.LastAttempt,
                };
            }
            return data;
        });
        PluginRegistryData snapshot = _registry.Current;
        if (snapshot.PendingNotices.Count == 0)
        {
            return;
        }

        foreach (PendingNoticeRecord notice in snapshot.PendingNotices.Where(notice => !notice.Consumed)
            .DistinctBy(notice => (notice.PluginId, notice.ActivationId)).ToList())
        {
            bool shown;
            try
            {
                shown = await _environment.ShowFaultNoticeAsync(new HostFaultNotice
                {
                    PluginId = notice.PluginId,
                    PluginName = notice.PluginId,
                    Version = notice.Version,
                    Reason = notice.Reason,
                    ActivationId = notice.ActivationId,
                    ExitConfirmed = notice.ExitConfirmed,
                    BudgetProtective = string.Equals(notice.Kind, "budget", StringComparison.Ordinal),
                }).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                ProgramLog?.Invoke($"Pending fault notice for '{notice.PluginId}' could not be displayed: {exception.Message}");
                shown = false;
            }
            if (shown)
            {
                _registry.Mutate(data =>
                {
                    data.PendingNotices.RemoveAll(pending => pending.PluginId == notice.PluginId && pending.ActivationId == notice.ActivationId);
                    if (data.Plugins.TryGetValue(notice.PluginId, out PluginRegistryEntry? entry))
                    {
                        data.Plugins[notice.PluginId] = entry with
                        {
                            LastAttempt = entry.LastAttempt?.ActivationId == notice.ActivationId
                                ? entry.LastAttempt with { RecoveryNotified = true } : entry.LastAttempt,
                            FaultDisabled = entry.FaultDisabled?.ActivationId == notice.ActivationId
                                ? entry.FaultDisabled with { Notified = true } : entry.FaultDisabled,
                        };
                    }
                });
            }
        }
    }
}
