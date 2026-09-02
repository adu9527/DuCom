using System.IO.Ports;
using System.Text;
using DuCom.Core.Ports;

namespace DuCom.Core.Tests.Ports;

public sealed class SerialPortSettingsTests
{
    [Fact]
    public void DefaultsMatchHighBaudReferenceBehavior()
    {
        SerialPortSettings settings = SerialPortSettings.Default("COM3");

        Assert.Equal(1_152_000, settings.BaudRate);
        Assert.Equal(8, settings.DataBits);
        Assert.Equal(StopBits.One, settings.StopBits);
        Assert.Equal(Parity.None, settings.Parity);
        Assert.Equal(Handshake.None, settings.Handshake);
        Assert.Equal(Encoding.UTF8.WebName, settings.EncodingName);
    }

    [Theory]
    [InlineData(0, 8)]
    [InlineData(115200, 4)]
    public void InvalidSettingsAreRejected(int baudRate, int dataBits)
    {
        SerialPortSettings settings = SerialPortSettings.Default("COM3") with { BaudRate = baudRate, DataBits = dataBits };

        Assert.Throws<ArgumentOutOfRangeException>(settings.Validate);
    }
}
