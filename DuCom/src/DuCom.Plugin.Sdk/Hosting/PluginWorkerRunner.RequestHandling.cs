using System.Text.Json;
using DuCom.Plugin.Dto;

namespace DuCom.Plugin;

public sealed partial class PluginWorkerRunner
{
    private async Task HandleRequestAsync(PluginWireMessage message)
    {
        JsonElement? data = message.Data;
        try
        {
            switch (message.Operation)
            {
                case PluginOps.WorkerInit:
                    _plugin = LoadPlugin();
                    _plugin.Runner = this;
                    _plugin.BindHostApi(_facades);
                    using (CancellationTokenSource init = new(TimeSpan.FromMilliseconds(Math.Max(_startup.Limits.StartupTimeoutMs - 2000, 2000))))
                    {
                        await _plugin.InitializeAsync(init.Token);
                    }

                    break;
                case PluginOps.PluginActivate:
                    if (_plugin is null)
                    {
                        throw new InvalidOperationException("Plugin is not initialized.");
                    }

                    using (CancellationTokenSource activation = new(TimeSpan.FromMilliseconds(Math.Max(_startup.Limits.ActivationTimeoutMs - 2000, 2000))))
                    {
                        PluginActivation result = await _plugin.ActivateAsync(activation.Token);
                        if (!UiContributionValidator.Validate(result, out string? error))
                        {
                            throw new InvalidOperationException(error);
                        }

                        await RespondAsync(message, JsonSerializer.SerializeToElement(SerializeActivation(result)));
                        return;
                    }
                case PluginOps.PluginDeactivate:
                    if (_plugin is not null)
                    {
                        using CancellationTokenSource grace = new(TimeSpan.FromMilliseconds(Math.Max(_startup.Limits.StopGraceMs - 500, 500)));
                        await _plugin.DeactivateAsync(grace.Token);
                    }

                    await RespondAsync(message, JsonSerializer.SerializeToElement(new { stopped = true }));
                    _shutdown = true;
                    return;
                case PluginOps.CommandInvoke:
                    if (_plugin is null)
                    {
                        throw new InvalidOperationException("Plugin is not initialized.");
                    }

                    CommandInvokeNotice notice = Deserialize<CommandInvokeNotice>(data);
                    using (CancellationTokenSource call = new(TimeSpan.FromMilliseconds(Math.Max(_startup.Limits.CallTimeoutMs - 200, 200))))
                    {
                        CommandInvokeOutcome outcome = await _plugin.OnCommandAsync(notice.CommandId, notice.Arg, notice.Values, call.Token);
                        await RespondAsync(message, JsonSerializer.SerializeToElement(outcome));
                        return;
                    }
                case PluginOps.SettingsApply:
                    if (_plugin is null)
                    {
                        throw new InvalidOperationException("Plugin is not initialized.");
                    }

                    SettingsApplyNotice applyNotice = Deserialize<SettingsApplyNotice>(data);
                    using (CancellationTokenSource call = new(TimeSpan.FromMilliseconds(Math.Max(_startup.Limits.CallTimeoutMs - 200, 200))))
                    {
                        SettingsApplyOutcome outcome = await _plugin.OnSettingsApplyAsync(applyNotice.Values, call.Token);
                        await RespondAsync(message, JsonSerializer.SerializeToElement(outcome));
                        return;
                    }
                case PluginOps.TaskCancel:
                    TaskCancelNotice cancelNotice = Deserialize<TaskCancelNotice>(data);
                    CancelPluginTask(cancelNotice.TaskId);
                    break;
                default:
                    await RespondErrorAsync(message, PluginErrorCode.UnsupportedOperation, $"Unknown operation '{message.Operation}'.");
                    return;
            }

            await RespondAsync(message, JsonSerializer.SerializeToElement(new { ok = true }));
        }
        catch (OperationCanceledException)
        {
            await RespondErrorAsync(message, PluginErrorCode.DeadlineExceeded, $"Operation '{message.Operation}' timed out in the worker.");
        }
        catch (Exception exception)
        {
            await RespondErrorAsync(message, PluginErrorCode.InternalError, exception.Message);
        }
    }

    private static Dictionary<string, object> SerializeActivation(PluginActivation activation) => new()
    {
        ["menus"] = activation.Menus,
        ["settingsPanels"] = activation.SettingsPanels,
        ["toolPages"] = activation.ToolPages,
        ["backgrounds"] = activation.BackgroundImages,
    };

    private static T Deserialize<T>(JsonElement? element) =>
        element is null
            ? throw new PluginHostException(PluginErrorCode.InvalidArgument, "Request payload is missing.")
            : element.Value.Deserialize<T>(DtoJson.Options)
                ?? throw new PluginHostException(PluginErrorCode.InvalidArgument, "Request payload is malformed.");

    public async Task<JsonElement?> RequestAsync(string operation, object? request, CancellationToken cancellationToken)
    {
        string requestId = $"w{Interlocked.Increment(ref _requestCounter)}";
        PluginWireMessage message = PluginWireMessage.Request(
            _startup.SessionId,
            _startup.ActivationId,
            requestId,
            operation,
            SerializePayload(request));
        TaskCompletionSource<PluginWireMessage> waiter = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingRequests[requestId] = waiter;
        try
        {
            await WriteMessageAsync(message, cancellationToken);
            PluginWireMessage response = await waiter.Task.WaitAsync(cancellationToken);
            if (response.Error is { } error)
            {
                throw new PluginHostException(error.Code, error.Message);
            }

            return response.Data;
        }
        finally
        {
            _pendingRequests.TryRemove(requestId, out _);
        }
    }

    public async Task NotifyAsync(string operation, object? payload, CancellationToken cancellationToken) =>
        await WriteMessageAsync(
            PluginWireMessage.Notify(_startup.SessionId, _startup.ActivationId, operation, SerializePayload(payload)),
            cancellationToken);

    private static JsonElement? SerializePayload(object? payload) =>
        payload is null
            ? null
            : JsonSerializer.SerializeToElement(payload, DtoJson.Options);

    private async Task RespondAsync(PluginWireMessage request, JsonElement data) =>
        await WriteMessageAsync(PluginWireMessage.Response(_startup.SessionId, _startup.ActivationId, request.RequestId ?? string.Empty, data));

    private async Task RespondErrorAsync(PluginWireMessage request, string code, string message) =>
        await WriteMessageAsync(PluginWireMessage.ErrorResponse(_startup.SessionId, _startup.ActivationId, request.RequestId ?? string.Empty, code, message));
}
