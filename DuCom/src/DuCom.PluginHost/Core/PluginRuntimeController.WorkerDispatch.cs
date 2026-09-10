using System.Text.Json;
using DuCom.Plugin;
using DuCom.PluginHost.Diagnostics;

namespace DuCom.PluginHost.Core;

public sealed partial class PluginRuntimeController
{
    private void OnWorkerMessage(PluginWireMessage message)
    {
        if (!string.Equals(message.SessionId, _sessionId, StringComparison.Ordinal)
            || !string.Equals(message.ActivationId, _activationId, StringComparison.Ordinal))
        {
            _diagnostics.Write(PluginLogLevel.Warning, $"Dropped a stale message for activation '{message.ActivationId}'.");
            return;
        }

        switch (message.Kind)
        {
            case WireKinds.Response:
                lock (_gate)
                {
                    if (message.RequestId is { } requestId
                        && _pendingHostRequests.Remove(requestId, out TaskCompletionSource<PluginWireMessage>? waiter))
                    {
                        waiter.TrySetResult(message);
                    }
                }

                break;
            case WireKinds.Request:
                if (string.IsNullOrWhiteSpace(message.RequestId))
                {
                    _ = SendWorkerErrorAsync(message, PluginErrorCode.InvalidArgument, "A worker request must include a request id.");
                    break;
                }
                lock (_gate)
                {
                    if (!_workerRequestIds.Add(message.RequestId))
                    {
                        _ = SendWorkerErrorAsync(message, PluginErrorCode.InvalidArgument, "Duplicate worker request id.");
                        break;
                    }
                }
                if (!_workerRequestSlots.Wait(0))
                {
                    lock (_gate) _workerRequestIds.Remove(message.RequestId);
                    _ = SendWorkerErrorAsync(message, PluginErrorCode.ResourceLimit, "Too many worker requests are in progress.");
                    break;
                }
                _ = DispatchWorkerRequestAsync(message);
                break;
            case WireKinds.Notification:
                HandleWorkerNotification(message);
                break;
        }
    }

    private async Task DispatchWorkerRequestAsync(PluginWireMessage message)
    {
        PluginWireMessage response;
        try
        {
            using CancellationTokenSource deadline = new(GetBrokerTimeout(message.Operation));
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, _activationCancellation?.Token ?? CancellationToken.None);
            JsonElement? result = _broker is null
                ? throw new PluginScopeException(PluginErrorCode.SessionExpired, "The activation is not active.")
                : await _broker.ExecuteAsync(message.Operation, message.Data, linked.Token).ConfigureAwait(false);
            response = PluginWireMessage.Response(
                _sessionId,
                _activationId,
                message.RequestId ?? string.Empty,
                result is null ? JsonSerializer.SerializeToElement(new { ok = true }) : result);
        }
        catch (PluginScopeException exception)
        {
            response = PluginWireMessage.ErrorResponse(_sessionId, _activationId, message.RequestId ?? string.Empty, exception.Code, exception.Message);
        }
        catch (OperationCanceledException) when (_activationCancellation?.IsCancellationRequested == true)
        {
            response = PluginWireMessage.ErrorResponse(_sessionId, _activationId, message.RequestId ?? string.Empty, PluginErrorCode.SessionExpired, "The activation has been stopped.");
        }
        catch (OperationCanceledException)
        {
            response = PluginWireMessage.ErrorResponse(_sessionId, _activationId, message.RequestId ?? string.Empty, PluginErrorCode.DeadlineExceeded, "The broker operation exceeded its deadline.");
            _ = FaultAsync($"Worker request '{message.Operation}' exceeded the host deadline.", exitConfirmed: true);
        }
        catch (Exception exception)
        {
            response = PluginWireMessage.ErrorResponse(_sessionId, _activationId, message.RequestId ?? string.Empty, PluginErrorCode.InternalError, exception.Message);
        }

        if (_pipe is not null)
        {
            try
            {
                await _pipe.SendControlMessageAsync(response, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }
        lock (_gate)
        {
            if (message.RequestId is not null) _workerRequestIds.Remove(message.RequestId);
        }
        _workerRequestSlots.Release();
    }

    private TimeSpan GetBrokerTimeout(string operation) => operation switch
    {
        PluginOps.FilesPickRead or PluginOps.FilesPickWrite => TimeSpan.FromMinutes(10),
        PluginOps.LogsSnapshot => TimeSpan.FromMinutes(2),
        PluginOps.OutputWrite => TimeSpan.FromSeconds(30),
        PluginOps.OutputCommit => TimeSpan.FromMinutes(5),
        PluginOps.FilesSnapshot => TimeSpan.FromMinutes(5),
        PluginOps.HelperStart => TimeSpan.FromSeconds(30),
        PluginOps.HelperCancel => TimeSpan.FromSeconds(15),
        _ => TimeSpan.FromMilliseconds(_limits.CallTimeoutMs),
    };

    private async Task SendWorkerErrorAsync(PluginWireMessage message, string code, string text)
    {
        try
        {
            if (_pipe is not null)
                await _pipe.SendControlMessageAsync(PluginWireMessage.ErrorResponse(_sessionId, _activationId, message.RequestId ?? string.Empty, code, text), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }

    private void HandleWorkerNotification(PluginWireMessage message)
    {
        switch (message.Operation)
        {
            case PluginOps.WorkerHeartbeat:
                _lastHeartbeatUtc = DateTimeOffset.UtcNow;
                break;
            case PluginOps.LogDiag:
                string level = message.Data?.TryGetProperty("level", out JsonElement levelElement) == true ? levelElement.GetString() ?? "info" : "info";
                string text = message.Data?.TryGetProperty("message", out JsonElement textElement) == true ? textElement.GetString() ?? string.Empty : string.Empty;
                _broker?.WriteDiagnostic(level, text);
                break;
            case PluginOps.TaskProgress:
                string taskId = message.Data?.TryGetProperty("taskId", out JsonElement taskElement) == true ? taskElement.GetString() ?? string.Empty : string.Empty;
                int? percent = message.Data?.TryGetProperty("percent", out JsonElement percentElement) == true && percentElement.ValueKind == JsonValueKind.Number ? percentElement.GetInt32() : null;
                _diagnostics.Write(PluginLogLevel.Info, $"task '{taskId}' progress {(percent.HasValue ? percent.Value.ToString() + "%" : "tick")}");
                break;
            default:
                _diagnostics.Write(PluginLogLevel.Warning, $"Unknown notification '{message.Operation}' ignored.");
                break;
        }
    }
}
