using DuCom.Services;
using DuCom.ViewModels;
using Xunit;

namespace DuCom.App.Tests;

public sealed class PortListComposerTests
{
    private static DiscoveredPort Serial(string name) => new(
        name, DiscoveredPortType.Serial, string.Empty, name, string.Empty,
        string.Empty, string.Empty, string.Empty, string.Empty);

    private static DiscoveredPort Usb(string name) => new(
        name, DiscoveredPortType.UsbSerial, string.Empty, name, string.Empty,
        string.Empty, string.Empty, string.Empty, string.Empty);

    private static DiscoveredPort Virtual(string name) => new(
        name, DiscoveredPortType.Virtual, string.Empty, name, string.Empty,
        string.Empty, string.Empty, string.Empty, string.Empty);

    private static IReadOnlyList<ComposedPort> Compose(
        IEnumerable<DiscoveredPort> ports,
        PortSortMode sortMode = PortSortMode.NameAscending,
        IEnumerable<string>? hidden = null,
        bool showSerial = true,
        bool showVirtual = true,
        bool showHidden = false,
        Func<string, bool>? isOpen = null)
    {
        DiscoveredPort[] list = [.. ports];
        return PortListComposer.Compose(
            [.. list.Select(port => port.PortName)],
            list.ToDictionary(port => port.PortName, port => port, StringComparer.OrdinalIgnoreCase),
            hidden?.ToList() ?? [],
            sortMode,
            showSerial,
            showVirtual,
            showHidden,
            isOpen ?? (_ => false));
    }

    [Fact]
    public void SortsNamesAscendingCaseInsensitiveByDefault()
    {
        IReadOnlyList<ComposedPort> ports = Compose([Serial("comC"), Serial("COMa"), Serial("comB")]);
        Assert.Equal(["COMa", "comB", "comC"], ports.Select(port => port.PortName).ToList());
    }

    [Fact]
    public void SortIsOrdinalLikeTheOriginalPicker()
    {
        // Documented pre-existing behavior: ordinal comparison puts COM10 before COM2.
        IReadOnlyList<ComposedPort> ports = Compose([Serial("COM2"), Serial("COM10")]);
        Assert.Equal(["COM10", "COM2"], ports.Select(port => port.PortName).ToList());
    }

    [Fact]
    public void NameDescendingReversesOrder()
    {
        IReadOnlyList<ComposedPort> ports = Compose(
            [Serial("COMa"), Serial("COMb")], PortSortMode.NameDescending);
        Assert.Equal(["COMb", "COMa"], ports.Select(port => port.PortName).ToList());
    }

    [Fact]
    public void ConnectedFirstPutsOpenPortsAhead()
    {
        IReadOnlyList<ComposedPort> ports = Compose(
            [Serial("COM2"), Serial("COM3"), Serial("COM4")],
            PortSortMode.ConnectedFirst,
            isOpen: name => name == "COM3");
        Assert.Equal(["COM3", "COM2", "COM4"], ports.Select(port => port.PortName).ToList());
    }

    [Fact]
    public void HiddenPortsAreDroppedWhenShowHiddenIsOff()
    {
        // Default showHidden=false drops hidden entries entirely.
        IReadOnlyList<ComposedPort> ports = Compose([Serial("COM2"), Serial("COM3")], hidden: ["COM2"]);
        Assert.Equal(["COM3"], ports.Select(port => port.PortName).ToList());
    }

    [Fact]
    public void HiddenPortsAppearWhenShowHiddenIsOn()
    {
        IReadOnlyList<ComposedPort> ports = Compose(
            [Serial("COM2"), Serial("COM3")], hidden: ["COM2"], showHidden: true);
        Assert.Equal(["COM2", "COM3"], ports.Select(port => port.PortName).ToList());
    }

    [Fact]
    public void VirtualPortsAreDroppedWhenVirtualIsHidden()
    {
        IReadOnlyList<ComposedPort> ports = Compose(
            [Serial("COM2"), Virtual("COMVN10")], showVirtual: false);
        Assert.Equal(["COM2"], ports.Select(port => port.PortName).ToList());
    }

    [Fact]
    public void SerialPortsAreDroppedWhenSerialIsHidden()
    {
        IReadOnlyList<ComposedPort> ports = Compose(
            [Serial("COM2"), Virtual("COMVN10")], showSerial: false);
        Assert.Equal(["COMVN10"], ports.Select(port => port.PortName).ToList());
    }

    [Theory]
    [InlineData((int)DiscoveredPortType.Serial, "COM")]
    [InlineData((int)DiscoveredPortType.UsbSerial, "USB")]
    [InlineData((int)DiscoveredPortType.Virtual, "VAR")]
    public void TypeLabelsFollowDiscoveryKind(int kind, string expectedLabel)
    {
        DiscoveredPort port = (DiscoveredPortType)kind switch
        {
            DiscoveredPortType.UsbSerial => Usb("COM5"),
            DiscoveredPortType.Virtual => Virtual("COM5"),
            _ => Serial("COM5"),
        };
        IReadOnlyList<ComposedPort> ports = Compose([port]);
        Assert.Equal(expectedLabel, ports.Single().TypeLabel);
    }
}
