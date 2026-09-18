using DuCom.PluginHost.Core;
using DuCom.PluginHost.Registry;

namespace DuCom.PluginHost;

public sealed partial class PluginSystemService
{
    private Task HandleFaultNoticeAsync(HostFaultNotice notice)
    {
        _registry.Mutate(data =>
        {
            data.PendingNotices.RemoveAll(pending => pending.PluginId == notice.PluginId);
            if (data.Plugins.TryGetValue(notice.PluginId, out PluginRegistryEntry? entry))
            {
                data.Plugins[notice.PluginId] = entry with
                {
                    FaultDisabled = entry.FaultDisabled is null
                        ? null
                        : entry.FaultDisabled with { Notified = true },
                    LastAttempt = entry.LastAttempt is null
                        ? null
                        : entry.LastAttempt with { RecoveryNotified = true },
                };
            }
            return data;
        });
        ProgramLog?.Invoke($"Plugin '{notice.PluginId}' fault recorded without a user popup: {notice.Reason}");
        FaultNotice?.Invoke(notice);
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    private Task ConsumePendingNoticesAsync()
    {
        _registry.Mutate(data =>
        {
            PendingNoticeRecord[] notices = [.. data.PendingNotices];
            data.PendingNotices.Clear();
            foreach (PendingNoticeRecord notice in notices)
            {
                if (data.Plugins.TryGetValue(notice.PluginId, out PluginRegistryEntry? entry))
                {
                    data.Plugins[notice.PluginId] = entry with
                    {
                        FaultDisabled = entry.FaultDisabled?.ActivationId == notice.ActivationId
                            ? entry.FaultDisabled with { Notified = true }
                            : entry.FaultDisabled,
                        LastAttempt = entry.LastAttempt?.ActivationId == notice.ActivationId
                            ? entry.LastAttempt with { RecoveryNotified = true }
                            : entry.LastAttempt,
                    };
                }
            }
            return data;
        });
        return Task.CompletedTask;
    }
}
