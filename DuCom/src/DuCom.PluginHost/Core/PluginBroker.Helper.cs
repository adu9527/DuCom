using DuCom.Plugin;
using DuCom.Plugin.Dto;

namespace DuCom.PluginHost.Core;

public sealed partial class PluginBroker
{
    private HelperTaskResult StartHelper(HelperStartRequest request)
    {
        RequirePermission(Permission.NativeHelpersExecute);
        return _helperTasks.Start(request);
    }

    private HelperTaskResult HelperStatus(HelperTaskRequest request)
    {
        RequirePermission(Permission.NativeHelpersExecute);
        return _helperTasks.Status(request.TaskId);
    }

    private async Task<HelperTaskResult> CancelHelperAsync(HelperTaskRequest request)
    {
        RequirePermission(Permission.NativeHelpersExecute);
        return await _helperTasks.CancelAsync(request.TaskId).ConfigureAwait(false);
    }
}
