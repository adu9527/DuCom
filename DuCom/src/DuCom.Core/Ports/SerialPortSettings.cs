using System.IO.Ports;
using System.Text;

namespace DuCom.Core.Ports;

public sealed record SerialPortSettings(
    string PortName,
    int BaudRate,
    int DataBits,
    StopBits StopBits,
    Parity Parity,
    Handshake Handshake,
    bool DtrEnable,
    bool RtsEnable,
    bool DiscardNull,
    string EncodingName,
    int ReadBufferSize,
    int WriteBufferSize)
{
    public static SerialPortSettings Default(string portName) => new(
        portName,
        1_152_000,
        8,
        StopBits.One,
        Parity.None,
        Handshake.None,
        DtrEnable: false,
        RtsEnable: false,
        DiscardNull: false,
        Encoding.UTF8.WebName,
        16 * 1024,
        4 * 1024);

    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(PortName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(BaudRate);
        if (DataBits is < 5 or > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(DataBits), DataBits, "Data bits must be between 5 and 8.");
        }

        if (StopBits == StopBits.None)
        {
            throw new ArgumentOutOfRangeException(nameof(StopBits), StopBits, "A physical stop bit is required.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(ReadBufferSize, 128);
        ArgumentOutOfRangeException.ThrowIfLessThan(WriteBufferSize, 128);
        _ = Encoding.GetEncoding(EncodingName);
    }
}

public interface IPortDiscovery
{
    IReadOnlyList<string> GetPortNames();
}

public sealed class SystemPortDiscovery : IPortDiscovery
{
    public IReadOnlyList<string> GetPortNames() => SerialPort.GetPortNames()
        .Order(StringComparer.OrdinalIgnoreCase)
        .ToArray();
}
