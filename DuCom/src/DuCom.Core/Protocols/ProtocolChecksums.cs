namespace DuCom.Core.Protocols;

public static class ProtocolChecksums
{
    public static ushort Crc16Modbus(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (byte value in data)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (ushort)((crc & 1) != 0 ? (crc >> 1) ^ 0xA001 : crc >> 1);
            }
        }

        return crc;
    }

    public static ushort Crc16Ccitt(ReadOnlySpan<byte> data, ushort initial = 0xFFFF)
    {
        ushort crc = initial;
        foreach (byte value in data)
        {
            crc ^= (ushort)(value << 8);
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (ushort)((crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1);
            }
        }

        return crc;
    }

    public static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte value in data)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
            }
        }

        return ~crc;
    }

    public static byte Xor(ReadOnlySpan<byte> data)
    {
        byte result = 0;
        foreach (byte value in data)
        {
            result ^= value;
        }

        return result;
    }

    public static byte Sum8(ReadOnlySpan<byte> data)
    {
        uint result = 0;
        foreach (byte value in data)
        {
            result += value;
        }

        return (byte)result;
    }

    public static ushort Sum16(ReadOnlySpan<byte> data)
    {
        uint result = 0;
        foreach (byte value in data)
        {
            result += value;
        }

        return (ushort)result;
    }
}
