using System.Text.Json;
using DuCom.Plugin;
using DuCom.Plugin.Dto;
using DuCom.PluginHost.Diagnostics;

namespace DuCom.PluginHost.Core;

public sealed partial class PluginRuntimeController
{
    internal void RegisterSerialSubscription(string subscriptionId, string sessionId)
    {
        lock (_gate)
        {
            _serialSubscriptions[subscriptionId] = new SerialSubscriptionState
            {
                SubscriptionId = subscriptionId,
                SessionId = sessionId,
            };
        }
    }

    internal void RemoveSerialSubscription(string subscriptionId)
    {
        lock (_gate)
        {
            _serialSubscriptions.Remove(subscriptionId);
        }
    }

    private void OnEnvironmentSessionClosed(object? sender, string sessionId)
    {
        if (_pipe is null || _state != PluginRuntimeState.Active)
        {
            return;
        }

        PluginWireMessage notice = PluginWireMessage.Notify(
            _sessionId,
            _activationId,
            PluginOps.SessionClosed,
            JsonSerializer.SerializeToElement(new SessionClosedNotice { SessionId = sessionId }));
        _ = _pipe.SendControlMessageAsync(notice, CancellationToken.None);
    }

    private void OnRawBlock(string sessionId, ReadOnlyMemory<byte> data, DateTimeOffset receivedAtUtc)
    {
        if (_state != PluginRuntimeState.Active || _pipe is null)
        {
            return;
        }

        SerialSubscriptionState[] subscriptions;
        lock (_gate)
        {
            if (_serialSubscriptions.Count == 0)
            {
                return;
            }

            subscriptions = [.. _serialSubscriptions.Values.Where(subscription => string.Equals(subscription.SessionId, sessionId, StringComparison.Ordinal))];
        }

        if (subscriptions.Length == 0)
        {
            return;
        }

        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        string b64 = Convert.ToBase64String(data.Span);
        foreach (SerialSubscriptionState subscription in subscriptions)
        {
            long sequence = subscription.NextSequence++;
            SerialDataNotice notice = new()
            {
                SubscriptionId = subscription.SubscriptionId,
                SessionId = sessionId,
                Sequence = sequence,
                ByteOffset = 0,
                ReceivedAtUtc = receivedAtUtc,
                B64 = b64,
                GapFrom = subscription.GapFrom,
            };
            subscription.GapFrom = null;

            byte[] frame = PluginWire.Encode(PluginWireMessage.Notify(_sessionId, _activationId, PluginOps.SerialData, JsonSerializer.SerializeToElement(notice)));
            bool delivered = _pipe.TryEnqueueEventFrame(frame);
            lock (_gate)
            {
                subscription.Window.Enqueue((now, delivered));
                if (!delivered)
                {
                    subscription.GapFrom = sequence;
                    subscription.DroppedBlocks++;
                }
            }

            if (!delivered)
            {
                _diagnostics.Write(PluginLogLevel.Warning, $"Receive side channel dropped block seq={sequence} (queue full).");
            }
        }
    }
}
