using DuCom.Plugin.Dto;

namespace DuCom.Plugin;

public sealed partial class PluginWorkerRunner
{
    private async Task HandleNotificationAsync(PluginWireMessage message)
    {
        switch (message.Operation)
        {
            case PluginOps.CommandPriority:
                if (_plugin is not null)
                {
                    CommandInvokeNotice command = Deserialize<CommandInvokeNotice>(message.Data);
                    _ = Task.Run(() => _plugin.OnCommandAsync(command.CommandId, command.Arg, command.Values, CancellationToken.None));
                }
                break;
            case PluginOps.SerialData:
                SerialDataNotice notice = Deserialize<SerialDataNotice>(message.Data);
                byte[] payload = string.IsNullOrEmpty(notice.B64) ? [] : Convert.FromBase64String(notice.B64);
                while (_serialQueue.Count >= _startup.Limits.SerialQueueBlocks)
                {
                    await _serialQueueSignal.WaitAsync(50, CancellationToken.None);
                    if (_shutdown)
                    {
                        return;
                    }
                }

                _serialQueue.Enqueue(new SerialDataEventArgs(payload, notice.SessionId, notice.Sequence, notice.ByteOffset, notice.ReceivedAtUtc));
                _serialQueueSignal.Release();
                break;
            case PluginOps.SessionClosed:
                SessionClosedNotice closed = Deserialize<SessionClosedNotice>(message.Data);
                _serialSink.RaiseClosed(closed.SessionId);
                _plugin?.OnSessionClosed(closed.SessionId);
                break;
        }
    }

    private async Task SerialPumpAsync()
    {
        while (!_shutdown)
        {
            await _serialQueueSignal.WaitAsync(200, CancellationToken.None);
            while (_serialQueue.TryDequeue(out SerialDataEventArgs? args))
            {
                if (_shutdown)
                {
                    return;
                }

                try
                {
                    _serialSink.RaiseData(args);
                    _plugin?.OnSerialData(args);
                }
                catch (Exception)
                {
                }
            }
        }
    }
}
