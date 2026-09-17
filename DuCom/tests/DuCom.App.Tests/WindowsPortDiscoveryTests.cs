using DuCom.Services;
using Xunit;

namespace DuCom.App.Tests;

public sealed class WindowsPortDiscoveryTests
{
    [Theory]
    [InlineData("BTHENUM\\{00001101-0000-1000-8000-00805F9B34FB}_VID&000102B0_PID&0000\\9&1&0", "Standard Serial over Bluetooth link (COM50)")]
    [InlineData("BTHMODEM\\00001101&00000000\\1", "Bluetooth Serial Port (COM51)")]
    [InlineData("BTHPORT\\PARAMETERS\\DEVICES\\001122334455", "Bluetooth COM Port (COM52)")]
    public void BluetoothSerialPortsAreVirtual(string pnpDeviceId, string caption)
    {
        Assert.Equal(DiscoveredPortType.Virtual, WindowsPortDiscovery.GetPortType(pnpDeviceId, caption));
    }

    [Theory]
    [InlineData("USB\\VID_1A86&PID_55D3\\5C7B040810", "USB-Enhanced-SERIAL CH343 (COM43)", (int)DiscoveredPortType.UsbSerial)]
    [InlineData("ACPI\\PNP0501\\0", "Communications Port (COM1)", (int)DiscoveredPortType.Serial)]
    [InlineData("ROOT\\PORTS\\0000", "JLVirtualJtagSerial Device (COM39)", (int)DiscoveredPortType.Virtual)]
    public void ExistingPortClassificationsRemainStable(
        string pnpDeviceId,
        string caption,
        int expected)
    {
        Assert.Equal((DiscoveredPortType)expected, WindowsPortDiscovery.GetPortType(pnpDeviceId, caption));
    }
}
