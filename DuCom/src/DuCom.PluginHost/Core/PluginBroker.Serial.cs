using DuCom.Plugin;
using DuCom.Plugin.Dto;

namespace DuCom.PluginHost.Core;

public sealed partial class PluginBroker
{
    private async Task<SerialLeaseResult> AcquireSerialLeaseAsync(SerialLeaseAcquireRequest request, CancellationToken cancellationToken)
    {
        RequirePermission(Permission.SerialLease);
        HostSerialLeaseResult result = await _environment.AcquireSerialLeaseAsync(new HostSerialLeaseRequest(
            _scope.Manifest.Id, _scope.ActivationId, request.TaskId, request.Port, request.DeviceIdentity, request.RestoreSession), cancellationToken).ConfigureAwait(false);
        return new SerialLeaseResult { LeaseId = result.LeaseId, Port = result.Port, State = result.State, SessionWasOpen = result.SessionWasOpen, Restored = result.Restored, Message = result.Message };
    }

    private async Task<SerialLeaseResult> ReleaseSerialLeaseAsync(SerialLeaseRequest request, CancellationToken cancellationToken)
    {
        RequirePermission(Permission.SerialLease);
        HostSerialLeaseResult result = await _environment.ReleaseSerialLeaseAsync(_scope.Manifest.Id, _scope.ActivationId, request.LeaseId, cancellationToken).ConfigureAwait(false);
        return new SerialLeaseResult { LeaseId = result.LeaseId, Port = result.Port, State = result.State, SessionWasOpen = result.SessionWasOpen, Restored = result.Restored, Message = result.Message };
    }

    private object SerialList()
    {
        RequirePermission(Permission.SerialRead);
        return new SerialListResult
        {
            Sessions = [.. _environment.GetSerialSessions().Select(session => new SerialSessionInfo
            {
                SessionId = session.SessionId,
                Port = session.Port,
                Open = session.Open,
            })],
        };
    }

    private object SerialPorts()
    {
        RequirePermission(Permission.SerialLease);
        return new SerialPortsResult
        {
            Ports = [.. _environment.GetSerialPorts().Select(port => new SerialPortInfo { Port = port.Port, DisplayName = port.DisplayName, VidPid = port.VidPid, DeviceIdentity = port.DeviceIdentity })],
        };
    }

    private object SubscribeSerial(SerialSubscribeRequest request)
    {
        RequirePermission(Permission.SerialRead);
        HostSerialSession session = _environment.GetSerialSessions()
            .FirstOrDefault(candidate => string.Equals(candidate.SessionId, request.SessionId, StringComparison.Ordinal))
            ?? throw new PluginScopeException(PluginErrorCode.NotFound, $"Session '{request.SessionId}' does not exist.");
        if (!session.Open)
        {
            throw new PluginScopeException(PluginErrorCode.InvalidArgument, $"Session '{request.SessionId}' is not open.");
        }

        string subscriptionId = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12));
        SerialSubscriptionAdded?.Invoke(subscriptionId, session.SessionId);
        return new SerialSubscribeResult
        {
            SubscriptionId = subscriptionId,
            QueueBlocks = _scope.Limits.SerialQueueBlocks,
            QueueMaxBytes = _scope.Limits.SerialQueueMaxBytes,
        };
    }

    private object UnsubscribeSerial(FilesTokenRequest request)
    {
        RequirePermission(Permission.SerialRead);
        SerialSubscriptionRemoved?.Invoke(this, request.Token);
        return new { ok = true };
    }
}
