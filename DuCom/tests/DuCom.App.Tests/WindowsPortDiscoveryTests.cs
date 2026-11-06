using DuCom.Services;
using DuCom.Core.Ports;
using DuCom.ViewModels;
using System.Runtime.InteropServices;
using Xunit;

namespace DuCom.App.Tests;

public sealed class WindowsPortDiscoveryTests
{
    [Fact]
    public void LiveDevnodesOverrideStaleWmiAndRegistryNames()
    {
        string[] ports = WindowsPortDiscovery.SelectPortNames(["COM8"], ["COM7", "COM8"], ["COM7"]);
        Assert.Equal(["COM8"], ports);
    }

    [Fact]
    public void SuccessfulEmptyPresenceSnapshotRemovesAllStalePorts()
    {
        Assert.Empty(WindowsPortDiscovery.SelectPortNames([], ["COM7"], ["COM7"]));
    }

    [Fact]
    public void RuntimePortsWithoutComCaptionArePreservedWhenNativeEnumerationFails()
    {
        Assert.Equal(["COM8", "COM9"], WindowsPortDiscovery.SelectPortNames(null, ["COM8", "com8"], ["COM9"]));
    }

    [Fact]
    public void PresentPortEnumeratorCanReadLiveWindowsDevnodes()
    {
        System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
        IReadOnlyCollection<string>? names = WindowsPresentPortEnumerator.GetPortNames();
        Console.WriteLine($"SetupAPI present-port enumeration: {stopwatch.Elapsed.TotalMilliseconds:F1} ms; names={string.Join(',', names ?? [])}");
        Assert.NotNull(names);
        Assert.All(names, name => Assert.True(Com0ComService.IsValidPortName(name)));
    }

    [Fact]
    public void EmptyDetailsSnapshotDoesNotTriggerSecondEnumeration()
    {
        EmptyDetailsDiscovery discovery = new();
        PortDiscoverySnapshot snapshot = MainViewModel.DiscoverPorts(discovery);
        Assert.Empty(snapshot.Names);
        Assert.Empty(snapshot.Details);
        Assert.Equal(1, discovery.DetailsCalls);
    }

    [Fact]
    public void NameOnlyDiscoveryRemainsSupported()
    {
        PortDiscoverySnapshot snapshot = MainViewModel.DiscoverPorts(new NameOnlyDiscovery());
        Assert.Equal(["COM8"], snapshot.Names);
        Assert.Empty(snapshot.Details);
    }

    [Fact]
    public void ComInterfaceNotificationRegistrationWorksForNativeWindow()
    {
        IntPtr window = CreateWindowEx(0, "STATIC", "DuCom serial notification test", 0, 0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        Assert.NotEqual(IntPtr.Zero, window);
        try
        {
            using WindowsPortNotifications notifications = new(window);
            Assert.True(notifications.IsRegistered);
            notifications.Dispose();
            Assert.False(notifications.IsRegistered);
        }
        finally
        {
            Assert.True(DestroyWindow(window));
        }
    }

    private sealed class EmptyDetailsDiscovery : IPortDiscovery, IPortDetailsProvider
    {
        public int DetailsCalls { get; private set; }
        public IReadOnlyList<string> GetPortNames() => throw new InvalidOperationException("An empty snapshot must not be enumerated twice.");
        public IReadOnlyDictionary<string, DiscoveredPort> GetPortDetails()
        {
            DetailsCalls++;
            return new Dictionary<string, DiscoveredPort>();
        }
    }

    private sealed class NameOnlyDiscovery : IPortDiscovery
    {
        public IReadOnlyList<string> GetPortNames() => ["COM8"];
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateWindowExW")]
    private static extern IntPtr CreateWindowEx(uint extendedStyle, string className, string title, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr window);

    [Theory]
    [InlineData(0x0219, 0x0007, true)]
    [InlineData(0x0219, 0x8000, true)]
    [InlineData(0x0219, 0x8004, true)]
    [InlineData(0x0219, 0x8001, false)] // QUERYREMOVE must not change presence.
    [InlineData(0x0219, 0x8002, false)] // QUERYREMOVEFAILED is not a removal.
    [InlineData(0x0001, 0x8000, false)]
    public void DeviceNotificationFilterOnlyRefreshesPresenceChanges(int message, int eventType, bool expected)
    {
        Assert.Equal(expected, WindowsPortNotifications.ShouldRefresh(message, new IntPtr(eventType)));
    }

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
