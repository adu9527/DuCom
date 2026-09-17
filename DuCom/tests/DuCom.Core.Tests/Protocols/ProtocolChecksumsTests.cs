using System.Text;
using DuCom.Core.Protocols;

namespace DuCom.Core.Tests.Protocols;

public sealed class ProtocolChecksumsTests
{
    private static readonly byte[] Check = Encoding.ASCII.GetBytes("123456789");

    [Fact]
    public void StandardCheckVectorsMatch()
    {
        Assert.Equal(0x4B37, ProtocolChecksums.Crc16Modbus(Check));
        Assert.Equal(0x29B1, ProtocolChecksums.Crc16Ccitt(Check));
        Assert.Equal(0xCBF43926U, ProtocolChecksums.Crc32(Check));
        Assert.Equal(0x31, ProtocolChecksums.Xor(Check));
        Assert.Equal(0xDD, ProtocolChecksums.Sum8(Check));
        Assert.Equal(0x01DD, ProtocolChecksums.Sum16(Check));
    }
}
