using System.Buffers.Binary;
using DuCom.Core.Protocols;
using DuCom.Core.Protocols.Modbus;

namespace DuCom.Core.Tests.Protocols;

public sealed class ModbusRtuDecoderTests
{
    [Fact]
    public void DecodesFrameAcrossChunksAndBuildsFieldDetails()
    {
        byte[] frame = WithCrc([0x01, 0x03, 0x00, 0x6B, 0x00, 0x03]);
        ModbusRtuDecoder decoder = new();

        Assert.Empty(decoder.Push(frame.AsSpan(0, 3), Context(1, 0)));
        ProtocolFrame decoded = Assert.Single(decoder.Push(frame.AsSpan(3), Context(2, 3)));

        Assert.Equal(ProtocolIntegrityStatus.Valid, decoded.Integrity.Status);
        Assert.Equal(0, decoded.StartByteOffset);
        Assert.Equal(frame.Length, decoded.EndByteOffset);
        Assert.Equal(1, decoded.FirstSequence);
        Assert.Equal(2, decoded.LastSequence);
        Assert.Contains(decoded.Fields, field => field.Name == "Start address" && Equals(field.Value, (ushort)107));
        Assert.Contains(decoded.Fields, field => field.Name == "Quantity" && Equals(field.Value, (ushort)3));
    }

    [Fact]
    public void BufferedFramesPreserveDirectionAndSourceGeneration()
    {
        Guid generation = Guid.NewGuid();
        byte[] frame = WithCrc([0x01, 0x06, 0x00, 0x01, 0x00, 0x03]);
        ModbusRtuDecoder decoder = new();

        decoder.Push(frame.AsSpan(0, 3), new(7, 10, 20, DateTimeOffset.UnixEpoch, ProtocolTrafficDirection.Tx, generation));
        ProtocolFrame decoded = Assert.Single(decoder.Push(frame.AsSpan(3), new(7, 11, 23, DateTimeOffset.UnixEpoch, ProtocolTrafficDirection.Tx, generation)));

        Assert.Equal(ProtocolTrafficDirection.Tx, decoded.Direction);
        Assert.Equal(generation, decoded.SourceGeneration);
    }

    [Theory]
    [InlineData(0x01)]
    [InlineData(0x02)]
    [InlineData(0x03)]
    [InlineData(0x04)]
    [InlineData(0x05)]
    [InlineData(0x06)]
    [InlineData(0x0F)]
    [InlineData(0x10)]
    public void SupportsRequiredRequestFunctions(byte function)
    {
        byte[] body = function is 0x0F or 0x10
            ? [0x11, function, 0x00, 0x10, 0x00, 0x02, 0x04, 0x00, 0x01, 0x00, 0x02]
            : [0x11, function, 0x00, 0x10, 0x00, 0x02];
        ProtocolFrame frame = Assert.Single(new ModbusRtuDecoder().Push(WithCrc(body), Context(1, 0)));
        Assert.Equal(ProtocolIntegrityStatus.Valid, frame.Integrity.Status);
        Assert.Equal(ProtocolFrameKind.Data, frame.Summary.Kind);
    }

    [Fact]
    public void DecodesResponsesExceptionMultipleFramesAndLeadingNoise()
    {
        byte[] response = WithCrc([0x01, 0x03, 0x04, 0x00, 0x0A, 0x00, 0x0B]);
        byte[] exception = WithCrc([0x01, 0x83, 0x02]);
        byte[] input = [0xFF, 0xEE, .. response, .. exception];

        IReadOnlyList<ProtocolFrame> frames = new ModbusRtuDecoder().Push(input, Context(1, 100));

        Assert.Equal(3, frames.Count);
        Assert.Equal(ProtocolFrameKind.Noise, frames[0].Summary.Kind);
        Assert.Equal([0xFF, 0xEE], frames[0].GetRawBytes());
        Assert.Equal("Read Holding Registers", frames[1].Summary.Type);
        Assert.StartsWith("Exception", frames[2].Summary.Type, StringComparison.Ordinal);
    }

    [Fact]
    public void ReportsInvalidCrcWithoutThrowing()
    {
        byte[] frame = WithCrc([0x01, 0x06, 0x00, 0x01, 0x00, 0x03]);
        frame[^1] ^= 0xFF;

        ProtocolFrame decoded = Assert.Single(new ModbusRtuDecoder().Push(frame, Context(1, 0)));

        Assert.Equal(ProtocolIntegrityStatus.Invalid, decoded.Integrity.Status);
        Assert.Contains(decoded.Diagnostics, diagnostic => diagnostic.Code == "modbus.crc.invalid");
    }

    [Fact]
    public void GapAndFlushReportIncompleteCandidates()
    {
        ModbusRtuDecoder decoder = new();
        decoder.Push([0x01, 0x03, 0x02], Context(1, 0));
        ProtocolFrame gap = Assert.Single(decoder.Gap(Context(2, 3), "capture loss"));
        Assert.Equal(ProtocolFrameKind.Incomplete, gap.Summary.Kind);
        Assert.Equal("capture loss", gap.Diagnostics[0].Message);

        decoder.Push([0x01, 0x03, 0x02], Context(3, 4));
        Assert.Equal(ProtocolFrameKind.Incomplete, Assert.Single(decoder.Flush(Context(4, 7))).Summary.Kind);
    }

    [Fact]
    public void ConfiguredInterFrameGapFlushesPriorCandidate()
    {
        ModbusRtuDecoder decoder = new(new ModbusRtuDecoderOptions(InterFrameGap: TimeSpan.FromMilliseconds(5)));
        decoder.Push([0x01, 0x03, 0x02], Context(1, 0, DateTimeOffset.UnixEpoch));
        byte[] valid = WithCrc([0x01, 0x06, 0x00, 0x01, 0x00, 0x03]);

        IReadOnlyList<ProtocolFrame> frames = decoder.Push(valid, Context(2, 3, DateTimeOffset.UnixEpoch.AddMilliseconds(10)));

        Assert.Equal(2, frames.Count);
        Assert.Equal(ProtocolFrameKind.Incomplete, frames[0].Summary.Kind);
        Assert.Equal(ProtocolIntegrityStatus.Valid, frames[1].Integrity.Status);
    }

    [Fact]
    public void RandomInputRemainsBoundedAndDoesNotThrow()
    {
        Random random = new(12345);
        ModbusRtuDecoder decoder = new(new ModbusRtuDecoderOptions(MaximumFrameLength: 256, MaximumBufferedBytes: 512, MaximumResyncBytesPerPush: 128));
        int outputCount = 0;
        for (int index = 0; index < 2_000; index++)
        {
            byte[] bytes = new byte[random.Next(0, 64)];
            random.NextBytes(bytes);
            outputCount += decoder.Push(bytes, Context(index + 1, index * 64L)).Count;
        }

        outputCount += decoder.Flush(Context(2_001, 128_000)).Count;
        Assert.True(outputCount > 0);
    }

    private static ProtocolDecoderContext Context(long sequence, long offset, DateTimeOffset? timestamp = null) =>
        new(1, sequence, offset, timestamp ?? DateTimeOffset.UnixEpoch.AddMilliseconds(sequence));

    private static byte[] WithCrc(byte[] body)
    {
        byte[] frame = new byte[body.Length + 2];
        body.CopyTo(frame, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(body.Length), ProtocolChecksums.Crc16Modbus(body));
        return frame;
    }
}
