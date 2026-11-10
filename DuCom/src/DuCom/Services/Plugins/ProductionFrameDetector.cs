using System.Buffers.Binary;
using DuCom.Plugins.Production;

namespace DuCom.Services.Plugins;

/// <summary>One verified frame-structure candidate extracted from a captured frame.</summary>
public sealed record ProductionFrameCandidate(
    int HeaderLength,
    int LengthOffset,
    int LengthSize,
    bool LengthLittleEndian,
    ChecksumKind Crc,
    bool CrcLittleEndian,
    int CrcStart,
    int CrcEndTrim,
    int LengthValue,
    int LengthAdjustment,
    int PayloadOffset)
{
    public byte[] Header(byte[] frame) => frame.AsSpan(0, HeaderLength).ToArray();
    public int TypicalLength => HeaderLength + LengthSize + (ProtocolCodec.CrcSize(Crc));

    /// <summary>True when 长度值 == 整帧 − 长度调整 exactly matches the canonical header+length+crc layout.</summary>
    public bool Canonical => LengthAdjustment == TypicalLength;
}

/// <summary>
/// Detects plausible frame structures from captured send/reply frames so operators do not
/// have to fill in header/length/CRC fields by hand. Every candidate's CRC is verified
/// against the captured bytes before it is proposed.
/// </summary>
public static class ProductionFrameDetector
{
    public static List<ProductionFrameCandidate> Detect(byte[] frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        List<ProductionFrameCandidate> candidates = [];
        if (frame.Length < 6) return candidates;

        int[] headerLengths = [2, 1, 3];
        int[] crcSizes = [1, 2, 0];
        bool[] twoByteOrders = [false, true];
        foreach (int headerLength in headerLengths)
        {
            int lengthOffset = headerLength;
            if (lengthOffset + 2 > frame.Length) continue;
            foreach (int crcSize in crcSizes)
            {
                foreach (ChecksumKind kind in KindsOfSize(crcSize))
                {
                    foreach (bool crcLittleEndian in crcSize == 2 ? twoByteOrders : [false])
                    {
                        ProtocolSettings settings = new()
                        {
                            TxHeader = Hex.Format(frame.AsSpan(0, headerLength).ToArray()),
                            RxHeader = Hex.Format(frame.AsSpan(0, headerLength).ToArray()),
                            LengthOffset = lengthOffset,
                            LengthSize = 1,
                            LengthAdjustment = 4,
                            PayloadOffset = headerLength + 1,
                            Checksum = kind,
                            CrcStart = 0,
                            CrcEndTrim = 0,
                            CrcLittleEndian = crcLittleEndian,
                            LengthLittleEndian = false,
                        };
                        int lengthValue = frame[lengthOffset];
                        int adjustment = frame.Length - lengthValue;
                        if (adjustment < headerLength + 1 + crcSize || adjustment > headerLength + 1 + crcSize + 4) continue;
                        if (!ProtocolCodec.CheckChecksum(settings, frame)) continue;
                        candidates.Add(new ProductionFrameCandidate(
                            headerLength, lengthOffset, 1, false, kind, crcLittleEndian, 0, 0, lengthValue, adjustment, headerLength + 1));
                    }
                }
            }

            // Two-byte length field right after the header (big- and little-endian).
            if (lengthOffset + 2 <= frame.Length - 1)
            {
                foreach (bool lengthLittleEndian in twoByteOrders)
                {
                    int crcSize = 1;
                    int lengthValue = lengthLittleEndian
                        ? BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(lengthOffset))
                        : BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(lengthOffset));
                    int adjustment = frame.Length - lengthValue;
                    if (adjustment != headerLength + 2 + crcSize || lengthValue > 255) continue;
                    ProtocolSettings settings = new()
                    {
                        TxHeader = Hex.Format(frame.AsSpan(0, headerLength).ToArray()),
                        RxHeader = Hex.Format(frame.AsSpan(0, headerLength).ToArray()),
                        LengthOffset = lengthOffset,
                        LengthSize = 2,
                        LengthAdjustment = adjustment,
                        PayloadOffset = headerLength + 2,
                        Checksum = ChecksumKind.Crc8Maxim,
                        CrcStart = 0,
                        CrcEndTrim = 0,
                        CrcLittleEndian = false,
                        LengthLittleEndian = lengthLittleEndian,
                    };
                    if (!ProtocolCodec.CheckChecksum(settings, frame)) continue;
                    candidates.Add(new ProductionFrameCandidate(
                        headerLength, lengthOffset, 2, lengthLittleEndian, ChecksumKind.Crc8Maxim, false, 0, 0, lengthValue, adjustment, headerLength + 2));
                }
            }
        }

        return candidates
            .Distinct()
            .OrderBy(candidate => candidate.HeaderLength == 2 ? 0 : 1)
            .ThenBy(candidate => candidate.Canonical ? 0 : 1)
            .ThenBy(candidate => candidate.Crc == ChecksumKind.Crc8Maxim ? 0 : 1)
            .ToList();
    }

    /// <summary>Candidates whose structure verifies on both captured frames.</summary>
    public static List<ProductionFrameCandidate> DetectPair(byte[] send, byte[] reply)
    {
        ArgumentNullException.ThrowIfNull(send);
        ArgumentNullException.ThrowIfNull(reply);
        List<ProductionFrameCandidate> common = [.. Detect(send).Where(candidate =>
            Detect(reply).Any(other =>
                other.HeaderLength == candidate.HeaderLength
                && other.Crc == candidate.Crc
                && other.CrcLittleEndian == candidate.CrcLittleEndian
                && other.LengthSize == candidate.LengthSize
                && other.LengthLittleEndian == candidate.LengthLittleEndian))];
        return common;
    }

    private static IEnumerable<ChecksumKind> KindsOfSize(int size) => size switch
    {
        0 => [ChecksumKind.None],
        1 => [ChecksumKind.Crc8Maxim, ChecksumKind.Crc8, ChecksumKind.Sum8, ChecksumKind.Xor8],
        _ => [ChecksumKind.Crc16Modbus, ChecksumKind.Crc16CcittFalse],
    };
}
