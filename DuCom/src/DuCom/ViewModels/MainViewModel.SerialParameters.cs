using DuCom.Core.Ports;

namespace DuCom.ViewModels;

public partial class MainViewModel
{
    private SerialPortSettings GetDefaultSerialSettings() => SerialPortSettings.Default(string.Empty) with
    {
        BaudRate = BaudRate,
        DataBits = DataBits,
        StopBits = StopBits,
        Parity = Parity,
        Handshake = Handshake,
        EncodingName = EncodingName,
    };

    private void ApplyDefaultSerialSettings(SerialPortSettings settings)
    {
        BaudRate = settings.BaudRate;
        DataBits = settings.DataBits;
        StopBits = settings.StopBits;
        Parity = settings.Parity;
        Handshake = settings.Handshake;
        EncodingName = settings.EncodingName;
    }
}
