using DuCom.Core.Protocols;
using DuCom.Core.Protocols.Custom;
using DuCom.Core.Sessions;
using DuCom.Services;
using Xunit;

namespace DuCom.App.Tests;

public sealed class ProtocolDecoderServiceTests
{
    [Fact]
    public async Task RxAndTxUseIndependentProfileDecodersAndPreserveTrafficMetadata()
    {
        SessionRawTapHub hub = new();
        Guid sourceGeneration = hub.BeginGeneration(1);
        ProtocolDecoderProfile profile = FixedLengthProfile(4);
        using ProtocolDecoderService service = new();
        service.Start(hub, profile);

        hub.PublishReceive(new byte[] { 1, 2 }, DateTimeOffset.UnixEpoch);
        hub.PublishTransmit(new byte[] { 10, 11 }, DateTimeOffset.UnixEpoch.AddMilliseconds(1));
        await WaitUntilAsync(() => service.Snapshot().ReceivedBytes == 4);
        Assert.Empty(service.Snapshot().Frames.Frames);

        hub.PublishReceive(new byte[] { 3, 4 }, DateTimeOffset.UnixEpoch.AddMilliseconds(2));
        hub.PublishTransmit(new byte[] { 12, 13 }, DateTimeOffset.UnixEpoch.AddMilliseconds(3));
        await WaitUntilAsync(() => service.Snapshot().Frames.Frames.Count == 2);

        ProtocolFrame[] frames = service.Snapshot().Frames.Frames.Select(item => item.Frame).ToArray();
        ProtocolFrame rx = Assert.Single(frames, frame => frame.Direction == ProtocolTrafficDirection.Rx);
        ProtocolFrame tx = Assert.Single(frames, frame => frame.Direction == ProtocolTrafficDirection.Tx);
        Assert.Equal([1, 2, 3, 4], rx.GetRawBytes());
        Assert.Equal([10, 11, 12, 13], tx.GetRawBytes());
        Assert.Equal(0, rx.StartByteOffset);
        Assert.Equal(0, tx.StartByteOffset);
        Assert.Equal(sourceGeneration, rx.SourceGeneration);
        Assert.Equal(sourceGeneration, tx.SourceGeneration);
        Assert.Equal(1, rx.DecoderFrameId);
        Assert.Equal(1, tx.DecoderFrameId);
    }

    [Fact]
    public async Task RuntimeGenerationChangeGapsOldStateAndResetsBothDirections()
    {
        SessionRawTapHub hub = new();
        Guid firstGeneration = hub.BeginGeneration(1);
        using ProtocolDecoderService service = new();
        service.Start(hub, FixedLengthProfile(4));
        hub.PublishReceive(new byte[] { 1, 2 }, DateTimeOffset.UnixEpoch);
        hub.PublishTransmit(new byte[] { 10, 11 }, DateTimeOffset.UnixEpoch);
        await WaitUntilAsync(() => service.Snapshot().ReceivedBytes == 4);

        Guid secondGeneration = hub.BeginGeneration(2);
        hub.PublishReceive(new byte[] { 3, 4, 5, 6 }, DateTimeOffset.UnixEpoch.AddSeconds(1));
        hub.PublishTransmit(new byte[] { 12, 13, 14, 15 }, DateTimeOffset.UnixEpoch.AddSeconds(1));
        await WaitUntilAsync(() => service.Snapshot().Frames.Frames.Count >= 4);

        ProtocolFrame[] frames = service.Snapshot().Frames.Frames.Select(item => item.Frame).ToArray();
        Assert.Equal(2, frames.Count(frame => frame.SourceGeneration == firstGeneration && frame.Summary.Kind == ProtocolFrameKind.Incomplete));
        ProtocolFrame[] current = frames.Where(frame => frame.SourceGeneration == secondGeneration && frame.Summary.Kind == ProtocolFrameKind.Data).ToArray();
        Assert.Equal(2, current.Length);
        Assert.All(current, frame => Assert.Equal(0, frame.StartByteOffset));
        Assert.Single(current.Select(frame => frame.Generation).Distinct());
        Assert.DoesNotContain(current, frame => frame.GetRawBytes().Contains((byte)1) || frame.GetRawBytes().Contains((byte)10));
        Assert.True(service.Snapshot().GapCount >= 1);
    }

    [Fact]
    public async Task RestartAndRepeatedStopDoNotAllowOldPumpToAffectNewRun()
    {
        SessionRawTapHub first = new();
        first.BeginGeneration(1);
        SessionRawTapHub second = new();
        Guid secondGeneration = second.BeginGeneration(1);
        using ProtocolDecoderService service = new();
        service.Start(first, FixedLengthProfile(2));
        first.PublishReceive(new byte[] { 1, 2 }, DateTimeOffset.UnixEpoch);
        await WaitUntilAsync(() => service.Snapshot().Frames.Frames.Count == 1);

        service.Start(second, FixedLengthProfile(2));
        first.PublishReceive(new byte[] { 3, 4 }, DateTimeOffset.UnixEpoch);
        second.PublishTransmit(new byte[] { 5, 6 }, DateTimeOffset.UnixEpoch);
        await WaitUntilAsync(() => service.Snapshot().Frames.Frames.Count == 2);
        await Task.Delay(50);

        ProtocolFrame latest = service.Snapshot().Frames.Frames[^1].Frame;
        Assert.Equal([5, 6], latest.GetRawBytes());
        Assert.Equal(ProtocolTrafficDirection.Tx, latest.Direction);
        Assert.Equal(secondGeneration, latest.SourceGeneration);
        service.Stop();
        service.Stop();
        Assert.Equal("Stopped", service.Snapshot().State);
    }

    private static ProtocolDecoderProfile FixedLengthProfile(int length)
    {
        CustomBinaryProfile custom = new()
        {
            Id = $"fixed-{length}",
            Name = "Fixed",
            MaximumFrameLength = 16,
            FixedLength = length,
        };
        return new ProtocolDecoderProfile(custom.Id, custom.Name, "customBinary", custom);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
}
