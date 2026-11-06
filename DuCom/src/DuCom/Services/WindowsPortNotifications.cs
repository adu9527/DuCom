using System.ComponentModel;
using System.Runtime.InteropServices;

namespace DuCom.Services;

/// <summary>Opt in to COM interface arrival/removal messages, including USB serial drivers.</summary>
internal sealed class WindowsPortNotifications : IDisposable
{
    private IntPtr _registration;

    internal WindowsPortNotifications(IntPtr window)
    {
        DeviceBroadcastInterface filter = new()
        {
            Size = Marshal.SizeOf<DeviceBroadcastInterface>(),
            DeviceType = 5, // DBT_DEVTYP_DEVICEINTERFACE
            ClassGuid = new Guid("86E0D1E0-8089-11D0-9CE4-08003E301F73"), // GUID_DEVINTERFACE_COMPORT
        };
        _registration = RegisterDeviceNotification(window, ref filter, 0); // DEVICE_NOTIFY_WINDOW_HANDLE
        if (_registration == IntPtr.Zero)
        {
            Program.DiagnosticLog?.Warning("Failed to register serial-device notifications.", new Win32Exception(Marshal.GetLastWin32Error()));
        }
    }

    internal bool IsRegistered => _registration != IntPtr.Zero;

    internal static bool ShouldRefresh(int message, IntPtr eventType) => message == 0x0219 // WM_DEVICECHANGE
        && eventType.ToInt64() is 0x0007 or 0x8000 or 0x8004; // DEVNODES_CHANGED / ARRIVAL / REMOVECOMPLETE

    public void Dispose()
    {
        if (_registration != IntPtr.Zero)
        {
            _ = UnregisterDeviceNotification(_registration);
            _registration = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceBroadcastInterface
    {
        public int Size;
        public int DeviceType;
        public int Reserved;
        public Guid ClassGuid;
        public short Name;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "RegisterDeviceNotificationW")]
    private static extern IntPtr RegisterDeviceNotification(IntPtr recipient, ref DeviceBroadcastInterface filter, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterDeviceNotification(IntPtr registration);
}
