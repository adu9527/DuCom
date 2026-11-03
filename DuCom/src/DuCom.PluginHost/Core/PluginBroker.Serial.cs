using DuCom.Plugin;
using DuCom.Plugin.Dto;

namespace DuCom.PluginHost.Core;

public sealed partial class PluginBroker
{
    private readonly Dictionary<string, string> _ownedSerialLeases = new(StringComparer.Ordinal);
    private readonly Dictionary<string, System.IO.Ports.SerialPort> _leasedPorts = new(StringComparer.Ordinal);
    private readonly object _serialIoGate = new();

    private void DisposeLeasedPorts()
    {
        lock (_serialIoGate)
        {
            foreach (var port in _leasedPorts.Values)
                try { port.Dispose(); } catch (IOException) { }
            _leasedPorts.Clear();
            _ownedSerialLeases.Clear();
        }
    }

    private void RequireIoLease(string leaseId)
    {
        RequirePermission(Permission.SerialLease);
        _scope.ThrowIfRevoked();
        if (!_ownedSerialLeases.ContainsKey(leaseId))
            throw new PluginScopeException(PluginErrorCode.PermissionDenied, "Serial lease does not belong to this activation.");
    }

    private object OpenLeasedPort(SerialLeaseIoRequest request)
    {
        lock (_serialIoGate)
        {
            RequireIoLease(request.LeaseId);
            if (request.BaudRate is < 300 or > 3000000) throw new PluginScopeException(PluginErrorCode.InvalidArgument, "Invalid baud rate.");
            if (_leasedPorts.ContainsKey(request.LeaseId)) throw new PluginScopeException(PluginErrorCode.InvalidArgument, "Port already open.");
            if (request.DataBits is < 5 or > 8 || !Enum.TryParse(request.Parity, out System.IO.Ports.Parity parity) || !Enum.IsDefined(parity)
                || !Enum.TryParse(request.StopBits, out System.IO.Ports.StopBits stopBits) || !Enum.IsDefined(stopBits) || stopBits == System.IO.Ports.StopBits.None)
                throw new PluginScopeException(PluginErrorCode.InvalidArgument, "串口参数无效");
            System.IO.Ports.SerialPort port = new(_ownedSerialLeases[request.LeaseId], request.BaudRate,
                parity, request.DataBits, stopBits) { ReadTimeout = 100, WriteTimeout = 1000, ReadBufferSize = 65536 };
            try { port.Open(); _leasedPorts.Add(request.LeaseId, port); }
            catch { port.Dispose(); throw; }
            return new { ok = true };
        }
    }

    private SerialLeaseIoResult ReadLeasedPort(SerialLeaseIoRequest request)
    {
        lock (_serialIoGate)
        {
            RequireIoLease(request.LeaseId);
            if (!_leasedPorts.TryGetValue(request.LeaseId, out var port)) throw new IOException("端口未打开");
            int count = Math.Min(port.BytesToRead, 16384);
            if (count == 0) return new();
            byte[] bytes = new byte[count];
            int read = port.Read(bytes, 0, count);
            if (read != count) Array.Resize(ref bytes, read);
            return new() { Bytes = bytes };
        }
    }

    private object WriteLeasedPort(SerialLeaseIoRequest request)
    {
        lock (_serialIoGate)
        {
            RequireIoLease(request.LeaseId);
            if (request.Bytes.Length is < 1 or > 4096) throw new PluginScopeException(PluginErrorCode.InvalidArgument, "Invalid serial write length.");
            if (!_leasedPorts.TryGetValue(request.LeaseId, out var port)) throw new IOException("端口未打开");
            port.Write(request.Bytes, 0, request.Bytes.Length);
            return new { ok = true };
        }
    }

    private async Task<SerialLeaseResult> AcquireSerialLeaseAsync(SerialLeaseAcquireRequest request, CancellationToken cancellationToken)
    {
        RequirePermission(Permission.SerialLease);
        HostSerialLeaseResult result = await _environment.AcquireSerialLeaseAsync(new HostSerialLeaseRequest(
            _scope.Manifest.Id, _scope.ActivationId, request.TaskId, request.Port, request.DeviceIdentity, request.RestoreSession), cancellationToken).ConfigureAwait(false);
        if (result.State == "leased")
            lock (_serialIoGate) _ownedSerialLeases[result.LeaseId] = result.Port;
        return new SerialLeaseResult { LeaseId = result.LeaseId, Port = result.Port, State = result.State, SessionWasOpen = result.SessionWasOpen, Restored = result.Restored, Message = result.Message };
    }

    private async Task<SerialLeaseResult> ReleaseSerialLeaseAsync(SerialLeaseRequest request, CancellationToken cancellationToken)
    {
        RequirePermission(Permission.SerialLease);
        lock (_serialIoGate)
        {
            RequireIoLease(request.LeaseId);
            if (_leasedPorts.Remove(request.LeaseId, out var port)) port.Dispose();
        }
        HostSerialLeaseResult result = await _environment.ReleaseSerialLeaseAsync(_scope.Manifest.Id, _scope.ActivationId, request.LeaseId, cancellationToken).ConfigureAwait(false);
        lock (_serialIoGate) _ownedSerialLeases.Remove(request.LeaseId);
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
