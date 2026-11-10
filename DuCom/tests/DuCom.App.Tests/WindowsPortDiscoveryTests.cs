using DuCom.Services;
using DuCom.Core.Ports;
using DuCom.ViewModels;
using System.Runtime.InteropServices;
using Xunit;

namespace DuCom.App.Tests;

public sealed class WindowsPortDiscoveryTests
{
    [Fact]
    public void NativeVirtualClassificationIsAvailableBeforeMetadataCompletes()
    {
        TaskCompletionSource<IReadOnlyDictionary<string, DiscoveredPort>> pending = new();
        DiscoveredPort native = new("COM8", DiscoveredPortType.Virtual, "com0com", "com0com", "", "", "", @"ROOT\PORTS\0000", "");
        WindowsPortDiscovery discovery = new(() => throw new InvalidOperationException("Do not enumerate twice"), () => [],
            new PortMetadataCache(() => pending.Task, () => DateTimeOffset.UnixEpoch, _ => { }),
            () => new Dictionary<string, DiscoveredPort> { ["COM8"] = native });

        IReadOnlyDictionary<string, DiscoveredPort> details = discovery.GetPortDetails();
        Assert.Equal(DiscoveredPortType.Virtual, details["COM8"].Type);
        Assert.Empty(PortListComposer.Compose(["COM8"], details, [], PortSortMode.NameAscending, true, false, false, _ => false));
        Assert.False(pending.Task.IsCompleted);
    }

    [Fact]
    public void NamesWithoutClassificationWaitRatherThanFlashInPhysicalOnlyList()
    {
        TaskCompletionSource<IReadOnlyDictionary<string, DiscoveredPort>> pending = new();
        WindowsPortDiscovery discovery = new(() => ["COM8"], () => [],
            new PortMetadataCache(() => pending.Task, () => DateTimeOffset.UnixEpoch, _ => { }));
        IReadOnlyDictionary<string, DiscoveredPort> details = discovery.GetPortDetails();
        Assert.Equal(DiscoveredPortType.Unknown, details["COM8"].Type);
        Assert.Empty(PortListComposer.Compose(["COM8"], details, [], PortSortMode.NameAscending, true, false, false, _ => false));
        pending.SetResult(new Dictionary<string, DiscoveredPort> { ["COM8"] = Detail("COM8", "USB") with { Type = DiscoveredPortType.Virtual } });
        details = discovery.GetPortDetails();
        Assert.Empty(PortListComposer.Compose(["COM8"], details, [], PortSortMode.NameAscending, true, false, false, _ => false));
    }

    [Fact]
    public void NativeGenerationDoesNotTakeCachedIdentityFromReusedComNumber()
    {
        IReadOnlyDictionary<string, DiscoveredPort> native = new Dictionary<string, DiscoveredPort>
        {
            ["COM8"] = Detail("COM8", "current USB") with { DeviceInstanceId = @"USB\CURRENT\1" },
        };
        WindowsPortDiscovery discovery = new(() => [], () => [],
            new PortMetadataCache(() => Task.FromResult<IReadOnlyDictionary<string, DiscoveredPort>>(
                new Dictionary<string, DiscoveredPort> { ["COM8"] = Detail("COM8", "old virtual") with { Type = DiscoveredPortType.Virtual, DeviceInstanceId = @"ROOT\OLD\1" } }),
                () => DateTimeOffset.UnixEpoch, _ => { }), () => native);
        _ = discovery.GetPortDetails();
        Assert.Equal("current USB", discovery.GetPortDetails()["COM8"].Description);
        Assert.Equal(DiscoveredPortType.UsbSerial, discovery.GetPortDetails()["COM8"].Type);
    }

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
        Assert.Equal(["COM8"], WindowsPortDiscovery.SelectPortNames(null, ["COM8", "com8"], ["COM9"]));
    }

    [Fact]
    public void PendingMetadataNeverBlocksPresenceOrResurrectsRemovedPorts()
    {
        DateTimeOffset now = DateTimeOffset.UnixEpoch;
        TaskCompletionSource<IReadOnlyDictionary<string, DiscoveredPort>> pending = new();
        int metadataCalls = 0;
        string[] present = ["COM8"];
        PortMetadataCache cache = new(() => { metadataCalls++; return pending.Task; }, () => now, _ => { });
        WindowsPortDiscovery discovery = new(() => present, () => throw new InvalidOperationException("no fallback expected"), cache);

        Assert.Equal(["COM8"], discovery.GetPortDetails().Keys);
        present = ["COM9"]; // Legacy/non-interface driver arrival while WMI is still stuck.
        now += TimeSpan.FromSeconds(10);
        Assert.Equal(["COM9"], discovery.GetPortDetails().Keys);
        Assert.Equal(1, metadataCalls); // Never overlap or queue unbounded WMI calls.

        pending.SetResult(new Dictionary<string, DiscoveredPort>
        {
            ["COM8"] = Detail("COM8", "removed USB"),
            ["COM9"] = Detail("COM9", "live USB"),
        });
        IReadOnlyDictionary<string, DiscoveredPort> enriched = discovery.GetPortDetails();
        Assert.Equal(["COM9"], enriched.Keys);
        Assert.Empty(enriched["COM9"].Description); // COM9 was not present when the old query began.
        Assert.Equal("live USB", discovery.GetPortDetails()["COM9"].Description); // The new generation query is eligible.
        present = [];
        Assert.Empty(discovery.GetPortDetails());
    }

    [Fact]
    public void ObservedRemovalInvalidatesIdentityIncludingAnOlderInFlightMetadataQuery()
    {
        DateTimeOffset now = DateTimeOffset.UnixEpoch;
        TaskCompletionSource<IReadOnlyDictionary<string, DiscoveredPort>> oldQuery = new();
        TaskCompletionSource<IReadOnlyDictionary<string, DiscoveredPort>> newQuery = new();
        int calls = 0;
        PortMetadataCache cache = new(() => ++calls == 1 ? oldQuery.Task : newQuery.Task, () => now, _ => { });
        string[] present = ["COM8"];
        WindowsPortDiscovery discovery = new(() => present, () => [], cache);
        _ = discovery.GetPortDetails();
        present = [];
        Assert.Empty(discovery.GetPortDetails());
        present = ["COM8"];
        oldQuery.SetResult(new Dictionary<string, DiscoveredPort> { ["COM8"] = Detail("COM8", "old identity") });
        Assert.Empty(discovery.GetPortDetails()["COM8"].Description);
        now += TimeSpan.FromSeconds(5);
        Assert.Empty(discovery.GetPortDetails()["COM8"].Description);
        Assert.Equal(2, calls);
        newQuery.SetResult(new Dictionary<string, DiscoveredPort> { ["COM8"] = Detail("COM8", "new identity") });
        Assert.Equal("new identity", discovery.GetPortDetails()["COM8"].Description);
    }

    [Fact]
    public void MetadataIsNotQueriedEveryFallbackTickAndNamesOnlyNeverQueriesIt()
    {
        DateTimeOffset now = DateTimeOffset.UnixEpoch;
        int calls = 0;
        PortMetadataCache cache = new(() =>
        {
            calls++;
            return Task.FromResult<IReadOnlyDictionary<string, DiscoveredPort>>(
                new Dictionary<string, DiscoveredPort> { ["COM8"] = Detail("COM8", "USB") });
        }, () => now, _ => { });
        WindowsPortDiscovery discovery = new(() => ["COM8"], () => [], cache);
        Assert.Equal(["COM8"], discovery.GetPortNames());
        Assert.Equal(0, calls);
        for (int tick = 0; tick < 30; tick++)
        {
            _ = discovery.GetPortDetails();
            now += TimeSpan.FromSeconds(2);
        }
        Assert.Equal(1, calls);
        _ = discovery.GetPortDetails();
        Assert.Equal(2, calls);
    }

    [Fact]
    public void UnchangedSnapshotsKeepUiItemsAndSelectionButMetadataChangesRebuild()
    {
        PortDiscoverySnapshot previous = new(["COM8"], new Dictionary<string, DiscoveredPort> { ["COM8"] = Detail("COM8", "USB") });
        PortDiscoverySnapshot same = new(["com8"], new Dictionary<string, DiscoveredPort> { ["COM8"] = Detail("COM8", "USB") });
        Assert.True(MainViewModel.SameDiscoveredPorts(previous, same));
        Assert.False(MainViewModel.SameDiscoveredPorts(previous, new(["COM8"],
            new Dictionary<string, DiscoveredPort> { ["COM8"] = Detail("COM8", "updated") })));
        Assert.False(MainViewModel.SameDiscoveredPorts(previous, new([], new Dictionary<string, DiscoveredPort>())));
    }

    private static DiscoveredPort Detail(string name, string description) => new(name, DiscoveredPortType.UsbSerial,
        description, description, "", "", "", "", "");

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
