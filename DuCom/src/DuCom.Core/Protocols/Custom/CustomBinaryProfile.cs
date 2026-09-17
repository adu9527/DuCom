using System.Text.Json.Serialization;

namespace DuCom.Core.Protocols.Custom;

public enum BinaryByteOrder
{
    LittleEndian,
    BigEndian,
}

#pragma warning disable CA1720 // Names intentionally match the declarative wire-format vocabulary.
public enum BinaryFieldType
{
    UInt8,
    Int8,
    UInt16,
    Int16,
    UInt32,
    Int32,
    UInt64,
    Int64,
    Float32,
    Float64,
    Ascii,
    Utf8,
    Bits,
}
#pragma warning restore CA1720

public enum BinaryChecksumAlgorithm
{
    Crc16Modbus,
    Crc16Ccitt,
    Crc32,
    Xor,
    Sum8,
    Sum16,
}

public sealed record CustomBinaryProfile
{
    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public int MaximumFrameLength { get; init; } = 4_096;

    public int? FixedLength { get; init; }

    public BinarySyncDefinition? Sync { get; init; }

    public BinaryLengthDefinition? Length { get; init; }

    public BinaryTerminatorDefinition? Terminator { get; init; }

    public BinaryEscapeDefinition? Escape { get; init; }

    public BinaryChecksumDefinition? Checksum { get; init; }

    public IReadOnlyList<BinaryFieldDefinition> Fields { get; init; } = [];
}

public sealed record BinarySyncDefinition
{
    [JsonConverter(typeof(HexByteArrayJsonConverter))]
    public byte[] Bytes { get; init; } = [];
}

public sealed record BinaryLengthDefinition
{
    public int Offset { get; init; }

    public int Size { get; init; } = 1;

    public BinaryByteOrder ByteOrder { get; init; }

    public bool IncludesHeader { get; init; } = true;

    public bool IncludesChecksum { get; init; } = true;

    public int Adjustment { get; init; }
}

public sealed record BinaryTerminatorDefinition
{
    [JsonConverter(typeof(HexByteArrayJsonConverter))]
    public byte[] Bytes { get; init; } = [];

    public bool IncludeInFrame { get; init; } = true;
}

public sealed record BinaryEscapeDefinition
{
    public byte EscapeByte { get; init; } = 0x7D;

    public byte XorMask { get; init; } = 0x20;

    public int StartOffset { get; init; }
}

public sealed record BinaryChecksumDefinition
{
    public BinaryChecksumAlgorithm Algorithm { get; init; }

    public int? FieldOffset { get; init; }

    public int? FieldOffsetFromEnd { get; init; }

    public BinaryByteOrder ByteOrder { get; init; }

    public int CoveredFrom { get; init; }

    public int? CoveredLength { get; init; }

    public bool CoveredToBeforeChecksum { get; init; } = true;
}

public sealed record BinaryFieldDefinition
{
    public string Name { get; init; } = string.Empty;

    public int Offset { get; init; }

    public BinaryFieldType Type { get; init; }

    public int? Length { get; init; }

    public BinaryByteOrder ByteOrder { get; init; }

    public int BitOffset { get; init; }

    public int BitLength { get; init; }

    public double Scale { get; init; } = 1;

    public double ValueOffset { get; init; }

    public string? Unit { get; init; }

    public string? Description { get; init; }

    public IReadOnlyList<BinaryFieldDefinition> Children { get; init; } = [];
}

public sealed record CustomBinaryProfileLimits(
    int MaximumFrameLength = 65_536,
    int MaximumFields = 256,
    int MaximumNestingDepth = 8,
    int MaximumSyncLength = 64,
    int MaximumTerminatorLength = 64,
    int MaximumResyncBytesPerPush = 65_536);

public sealed record CustomBinaryProfileValidationResult(IReadOnlyList<ProtocolDiagnostic> Diagnostics)
{
    public bool IsValid => Diagnostics.All(item => item.Severity != ProtocolDiagnosticSeverity.Error);
}
