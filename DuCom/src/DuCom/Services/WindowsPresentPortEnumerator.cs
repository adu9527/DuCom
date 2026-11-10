using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace DuCom.Services;

/// <summary>Reads COM names from live devnodes, not the potentially stale SERIALCOMM/WMI caches.</summary>
internal static class WindowsPresentPortEnumerator
{
    private const uint PresentAllClasses = 0x00000002 | 0x00000004;
    private const int NoMoreItems = 259;
    private static readonly IntPtr InvalidHandle = new(-1);

    internal static IReadOnlyCollection<string>? GetPortNames() => GetPortDetails()?.Keys.ToArray();

    internal static IReadOnlyDictionary<string, DiscoveredPort>? GetPortDetails()
    {
        IntPtr devices = SetupDiGetClassDevs(IntPtr.Zero, null, IntPtr.Zero, PresentAllClasses);
        if (devices == InvalidHandle)
        {
            Program.DiagnosticLog?.Warning("Failed to enumerate present serial devices.", new Win32Exception(Marshal.GetLastWin32Error()));
            return null;
        }

        try
        {
            Dictionary<string, DiscoveredPort> names = new(StringComparer.OrdinalIgnoreCase);
            for (uint index = 0; ; index++)
            {
                DeviceInfoData info = new() { Size = (uint)Marshal.SizeOf<DeviceInfoData>() };
                if (!SetupDiEnumDeviceInfo(devices, index, ref info))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error != NoMoreItems)
                    {
                        Program.DiagnosticLog?.Warning("Failed to enumerate present serial devices.", new Win32Exception(error));
                        return null; // A partial list must never be authoritative for removals.
                    }

                    return names;
                }

                IntPtr keyHandle = SetupDiOpenDevRegKey(devices, ref info, 1, 0, 1, 0x0001);
                if (keyHandle == InvalidHandle)
                {
                    continue; // Most devnodes have no device-parameters key.
                }

                using SafeRegistryHandle handle = new(keyHandle, ownsHandle: true);
                using RegistryKey key = RegistryKey.FromHandle(handle);
                if (key.GetValue("PortName") is string name && Com0ComService.IsValidPortName(name))
                {
                    string portName = name.ToUpperInvariant();
                    char[] instanceId = new char[1024];
                    string id = SetupDiGetDeviceInstanceId(devices, ref info, instanceId, (uint)instanceId.Length, out _)
                        ? new string(instanceId).TrimEnd('\0') : string.Empty;
                    string caption = ReadProperty(devices, ref info, 12); // SPDRP_FRIENDLYNAME
                    if (string.IsNullOrEmpty(caption)) caption = ReadProperty(devices, ref info, 0); // SPDRP_DEVICEDESC
                    names[portName] = new DiscoveredPort(portName,
                        WindowsPortDiscovery.GetPortType(id, caption), caption,
                        string.IsNullOrEmpty(caption) ? portName : caption,
                        ReadProperty(devices, ref info, 11), string.Empty, string.Empty, id,
                        ReadProperty(devices, ref info, 13));
                }
            }
        }
        catch (Exception exception) when (exception is System.IO.IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Program.DiagnosticLog?.Warning("Failed to read present serial-device port names.", exception);
            return null;
        }
        finally
        {
            _ = SetupDiDestroyDeviceInfoList(devices);
        }
    }

    private static string ReadProperty(IntPtr devices, ref DeviceInfoData info, uint property)
    {
        byte[] buffer = new byte[4096];
        return SetupDiGetDeviceRegistryProperty(devices, ref info, property, out _, buffer, (uint)buffer.Length, out _)
            ? Encoding.Unicode.GetString(buffer).TrimEnd('\0') : string.Empty;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetupDiGetDeviceInstanceIdW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInstanceId(IntPtr devices, ref DeviceInfoData info, [Out] char[] instanceId, uint size, out uint requiredSize);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetupDiGetDeviceRegistryPropertyW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceRegistryProperty(IntPtr devices, ref DeviceInfoData info, uint property, out uint registryType, byte[] buffer, uint size, out uint requiredSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfoData
    {
        public uint Size;
        public Guid ClassGuid;
        public uint DevInst;
        public UIntPtr Reserved;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetupDiGetClassDevsW")]
    private static extern IntPtr SetupDiGetClassDevs(IntPtr classGuid, string? enumerator, IntPtr parent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr devices, uint index, ref DeviceInfoData info);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiOpenDevRegKey(IntPtr devices, ref DeviceInfoData info, uint scope, uint profile, uint keyType, uint access);

    [DllImport("setupapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr devices);
}
