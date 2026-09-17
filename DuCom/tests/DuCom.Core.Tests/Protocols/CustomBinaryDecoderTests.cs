using System.Buffers.Binary;
using DuCom.Core.Protocols;
using DuCom.Core.Protocols.Custom;

namespace DuCom.Core.Tests.Protocols;

public sealed class CustomBinaryDecoderTests
{
    [Fact]
    public void FixedLengthSyncDecoderHandlesNoiseSplitsAndChecksum()
    {
        CustomBinaryProfile profile = new()
        {
            Id = "fixed",
            Name = "Fixed",
            MaximumFrameLength = 16,
            FixedLength = 6,
            Sync = new BinarySyncDefinition { Bytes = [0xAA, 0x55] },
            Checksum = new BinaryChecksumDefinition
            {
                Algorithm = BinaryChecksumAlgorithm.Sum8,
                FieldOffsetFromEnd = 1,
                CoveredFrom = 0,
            },
            Fields = [new BinaryFieldDefinition { Name = "value", Offset = 2, Type = BinaryFieldType.UInt16, ByteOrder = BinaryByteOrder.LittleEndian, Scale = 0.5, Unit = "V" }],
        };
        byte[] frame = [0xAA, 0x55, 0x34, 0x12, 0x99, 0];
        frame[^1] = ProtocolChecksums.Sum8(frame.AsSpan(0, frame.Length - 1));
        CustomBinaryDecoder decoder = new(profile);

        Assert.Single(decoder.Push([0x00, 0xAA], Context(1, 0)));
        ProtocolFrame decoded = Assert.Single(decoder.Push(frame.AsSpan(1), Context(2, 2)));

        Assert.Equal(ProtocolIntegrityStatus.Valid, decoded.Integrity.Status);
        ProtocolField value = Assert.Single(decoded.Fields);
        Assert.Equal(2330D, value.Value);
        Assert.Equal("V", value.Unit);
    }

    [Fact]
    public void BufferedFramesPreserveDirectionAndSourceGeneration()
    {
        CustomBinaryProfile profile = new() { Id = "direction", Name = "Direction", MaximumFrameLength = 8, FixedLength = 4 };
        Guid generation = Guid.NewGuid();
        CustomBinaryDecoder decoder = new(profile);

        decoder.Push([1, 2], new(3, 4, 10, DateTimeOffset.UnixEpoch, ProtocolTrafficDirection.Tx, generation));
        ProtocolFrame frame = Assert.Single(decoder.Push([3, 4], new(3, 5, 12, DateTimeOffset.UnixEpoch, ProtocolTrafficDirection.Tx, generation)));

        Assert.Equal(ProtocolTrafficDirection.Tx, frame.Direction);
        Assert.Equal(generation, frame.SourceGeneration);
    }

    [Fact]
    public void LengthFrameDecodesEndianSignedFloatTextBitsAndNestedFields()
    {
        CustomBinaryProfile profile = new()
        {
            Id = "typed",
            Name = "Typed",
            MaximumFrameLength = 32,
            Sync = new BinarySyncDefinition { Bytes = [0xA5] },
            Length = new BinaryLengthDefinition { Offset = 1, Size = 1, IncludesHeader = true },
            Fields =
            [
                new BinaryFieldDefinition { Name = "signed", Offset = 2, Type = BinaryFieldType.Int16, ByteOrder = BinaryByteOrder.BigEndian },
                new BinaryFieldDefinition { Name = "float", Offset = 4, Type = BinaryFieldType.Float32, ByteOrder = BinaryByteOrder.LittleEndian },
                new BinaryFieldDefinition { Name = "text", Offset = 8, Type = BinaryFieldType.Ascii, Length = 2 },
                new BinaryFieldDefinition { Name = "flags", Offset = 10, Type = BinaryFieldType.Bits, Length = 1, BitOffset = 1, BitLength = 3,
                    Children = [new BinaryFieldDefinition { Name = "child", Offset = 10, Type = BinaryFieldType.UInt8 }] },
            ],
        };
        byte[] frame = new byte[11];
        frame[0] = 0xA5;
        frame[1] = (byte)frame.Length;
        BinaryPrimitives.WriteInt16BigEndian(frame.AsSpan(2), -1234);
        BinaryPrimitives.WriteSingleLittleEndian(frame.AsSpan(4), 1.5F);
        frame[8] = (byte)'O';
        frame[9] = (byte)'K';
        frame[10] = 0b0000_1010;

        ProtocolFrame decoded = Assert.Single(new CustomBinaryDecoder(profile).Push(frame, Context(1, 0)));

        Assert.Equal(-1234L, decoded.Fields[0].Value);
        Assert.Equal(1.5D, decoded.Fields[1].Value);
        Assert.Equal("OK", decoded.Fields[2].Value);
        Assert.Equal(5L, decoded.Fields[3].Value);
        Assert.Single(decoded.Fields[3].Children!);
    }

    [Fact]
    public void TerminatorAndEscapeSupportMultipleFramesAndExcludedTerminator()
    {
        CustomBinaryProfile profile = new()
        {
            Id = "lines",
            Name = "Lines",
            MaximumFrameLength = 16,
            Terminator = new BinaryTerminatorDefinition { Bytes = [0x0A], IncludeInFrame = false },
            Escape = new BinaryEscapeDefinition { EscapeByte = 0x7D, XorMask = 0x20 },
            Fields = [new BinaryFieldDefinition { Name = "text", Offset = 0, Type = BinaryFieldType.Ascii, Length = 2 }],
        };
        byte[] input = [(byte)'A', 0x7D, 0x2A, 0x0A, (byte)'O', (byte)'K', 0x0A];

        IReadOnlyList<ProtocolFrame> frames = new CustomBinaryDecoder(profile).Push(input, Context(1, 0));

        Assert.Equal(2, frames.Count);
        Assert.Equal([(byte)'A', 0x7D, 0x2A, 0x0A], frames[0].GetRawBytes());
        Assert.Equal(4, frames[0].EndByteOffset);
        Assert.Equal("A\n", frames[0].Fields[0].Value);
        Assert.Equal([(byte)'O', (byte)'K', 0x0A], frames[1].GetRawBytes());
    }

    [Theory]
    [InlineData(BinaryChecksumAlgorithm.Crc16Modbus)]
    [InlineData(BinaryChecksumAlgorithm.Crc16Ccitt)]
    [InlineData(BinaryChecksumAlgorithm.Crc32)]
    [InlineData(BinaryChecksumAlgorithm.Xor)]
    [InlineData(BinaryChecksumAlgorithm.Sum8)]
    [InlineData(BinaryChecksumAlgorithm.Sum16)]
    public void SupportsEveryChecksumAlgorithm(BinaryChecksumAlgorithm algorithm)
    {
        int size = algorithm is BinaryChecksumAlgorithm.Crc32 ? 4 : algorithm is BinaryChecksumAlgorithm.Xor or BinaryChecksumAlgorithm.Sum8 ? 1 : 2;
        CustomBinaryProfile profile = new()
        {
            Id = algorithm.ToString(),
            Name = algorithm.ToString(),
            MaximumFrameLength = 16,
            FixedLength = 3 + size,
            Checksum = new BinaryChecksumDefinition
            {
                Algorithm = algorithm,
                FieldOffsetFromEnd = size,
                ByteOrder = BinaryByteOrder.LittleEndian,
                CoveredFrom = 0,
            },
        };
        byte[] frame = new byte[3 + size];
        frame[0] = 1;
        frame[1] = 2;
        frame[2] = 3;
        ulong checksum = Calculate(algorithm, frame.AsSpan(0, 3));
        for (int index = 0; index < size; index++) frame[3 + index] = (byte)(checksum >> (8 * index));

        ProtocolFrame decoded = Assert.Single(new CustomBinaryDecoder(profile).Push(frame, Context(1, 0)));
        Assert.Equal(ProtocolIntegrityStatus.Valid, decoded.Integrity.Status);
    }

    [Fact]
    public void GapFlushAndRandomInputAreSafeAndBounded()
    {
        CustomBinaryProfile profile = new()
        {
            Id = "safe",
            Name = "Safe",
            MaximumFrameLength = 32,
            Sync = new BinarySyncDefinition { Bytes = [0xAA, 0x55] },
            FixedLength = 8,
        };
        CustomBinaryDecoder decoder = new(profile, new CustomBinaryProfileLimits(MaximumResyncBytesPerPush: 64));
        Random random = new(42);
        for (int index = 0; index < 1_000; index++)
        {
            byte[] bytes = new byte[random.Next(0, 64)];
            random.NextBytes(bytes);
            _ = decoder.Push(bytes, Context(index + 1, index * 64L));
        }

        _ = decoder.Gap(Context(1_001, 64_000));
        decoder.Push([0xAA, 0x55, 1], Context(1_002, 64_001));
        Assert.Equal(ProtocolFrameKind.Incomplete, Assert.Single(decoder.Flush(Context(1_003, 64_004))).Summary.Kind);
    }

    private static ulong Calculate(BinaryChecksumAlgorithm algorithm, ReadOnlySpan<byte> bytes) => algorithm switch
    {
        BinaryChecksumAlgorithm.Crc16Modbus => ProtocolChecksums.Crc16Modbus(bytes),
        BinaryChecksumAlgorithm.Crc16Ccitt => ProtocolChecksums.Crc16Ccitt(bytes),
        BinaryChecksumAlgorithm.Crc32 => ProtocolChecksums.Crc32(bytes),
        BinaryChecksumAlgorithm.Xor => ProtocolChecksums.Xor(bytes),
        BinaryChecksumAlgorithm.Sum8 => ProtocolChecksums.Sum8(bytes),
        BinaryChecksumAlgorithm.Sum16 => ProtocolChecksums.Sum16(bytes),
        _ => throw new ArgumentOutOfRangeException(nameof(algorithm)),
    };

    private static ProtocolDecoderContext Context(long sequence, long offset) =>
        new(1, sequence, offset, DateTimeOffset.UnixEpoch.AddMilliseconds(sequence));
}
