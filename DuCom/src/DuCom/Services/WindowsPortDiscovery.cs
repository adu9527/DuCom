using System.IO;
using System.IO.Ports;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using DuCom.Core.Ports;

namespace DuCom.Services;

internal enum DiscoveredPortType
{
    Serial,
    UsbSerial,
    Virtual,
}

internal sealed record DiscoveredPort(
    string PortName,
    DiscoveredPortType Type,
    string Description,
    string DeviceName,
    string Manufacturer,
    string VidPid,
    string SerialNumber,
    string DeviceInstanceId,
    string LocationInfo);

internal interface IPortDetailsProvider
{
    IReadOnlyDictionary<string, DiscoveredPort> GetPortDetails();
}

internal sealed class WindowsPortDiscovery : IPortDiscovery, IPortDetailsProvider
{
    // Caption is already resolved by WMI (e.g. "USB-Enhanced-SERIAL CH344 (COM31)"),
    // which avoids having to translate the indirect "@oemNN.inf,..." registry strings.
    // The LIKE filter keeps the result set to COM-attached devices so the query stays fast.
    private const string PortInfoSelectSql =
        "SELECT Caption, Name, Manufacturer, PNPDeviceID, DeviceID FROM Win32_PnPEntity WHERE Caption LIKE '%(COM%'";

    private static readonly Regex ParenthesisedComPort = new(
        @"\(\s*(COM\d+)\s*\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex BareComPort = new(
        @"\b(COM\d+)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public IReadOnlyList<string> GetPortNames() => GetPortDetails().Keys
        .Order(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public IReadOnlyDictionary<string, DiscoveredPort> GetPortDetails()
    {
        Dictionary<string, DiscoveredPort> ports = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, PnPDeviceInfo> pnpDevices = QueryPnPDevices(out bool pnpQuerySucceeded);

        // A successful PnP query is authoritative for device presence. SerialPort may return
        // stale SERIALCOMM registry mappings after a virtual-port driver/device is removed.
        IEnumerable<string> runtimePorts = SerialPort.GetPortNames().Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (string portName in runtimePorts)
        {
            if (pnpQuerySucceeded && !pnpDevices.ContainsKey(portName))
            {
                Program.DiagnosticLog?.Information($"Ignoring stale serial-port mapping. Port={portName}");
                continue;
            }

            ports[portName] = new DiscoveredPort(
                portName,
                DiscoveredPortType.Serial,
                string.Empty,
                portName,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty);
        }

        // Resolve PnP device metadata for each port, then enrich the seed entries.
        foreach ((string portName, PnPDeviceInfo info) in pnpDevices)
        {
            string description = TrimComSuffix(info.Caption);
            ports[portName] = new DiscoveredPort(
                portName,
                GetPortType(info.PnpDeviceId, info.Caption),
                description,
                description,
                info.Manufacturer,
                ExtractVidPid(info.PnpDeviceId),
                ExtractInstanceId(info.PnpDeviceId),
                info.PnpDeviceId,
                ReadLocationInfo(info.PnpDeviceId));
        }

        return ports;
    }

    private static Dictionary<string, PnPDeviceInfo> QueryPnPDevices(out bool succeeded)
    {
        Dictionary<string, PnPDeviceInfo> byPort = new(StringComparer.OrdinalIgnoreCase);
        succeeded = false;
        try
        {
            using var searcher = new ManagementObjectSearcher(PortInfoSelectSql);
            using ManagementObjectCollection results = searcher.Get();
            succeeded = true;
            foreach (ManagementBaseObject p in results)
            {
                string caption = Safe(p["Caption"]);
                string name = Safe(p["Name"]);
                string combine = string.IsNullOrEmpty(caption) ? name : caption;
                if (string.IsNullOrEmpty(combine))
                {
                    continue;
                }

                string? portName = ExtractComPort(combine);
                if (portName is null || !Com0ComService.IsValidPortName(portName))
                {
                    continue;
                }

                string pnp = Safe(p["PNPDeviceID"]);
                if (string.IsNullOrEmpty(pnp))
                {
                    pnp = Safe(p["DeviceID"]);
                }

                byPort[portName] = new PnPDeviceInfo(combine, Safe(p["Manufacturer"]), pnp);
            }
        }
        catch (Exception exception) when (exception is ManagementException or COMException or UnauthorizedAccessException)
        {
            Program.DiagnosticLog?.Warning("Failed to query Win32_PnPEntity for serial-port details.", exception);
        }

        return byPort;
    }

    private static string Safe(object? value) => value?.ToString()?.Trim() ?? string.Empty;

    private static string? ExtractComPort(string text)
    {
        Match match = ParenthesisedComPort.Match(text);
        if (match.Success)
        {
            return match.Groups[1].Value.ToUpperInvariant();
        }

        match = BareComPort.Match(text);
        return match.Success ? match.Groups[1].Value.ToUpperInvariant() : null;
    }

    private static string TrimComSuffix(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        return Regex.Replace(text, @"\s*\(COM\d+\)\s*$", string.Empty, RegexOptions.IgnoreCase).Trim();
    }

    private static DiscoveredPortType GetPortType(string? pnpDeviceId, string? caption)
    {
        string id = pnpDeviceId ?? string.Empty;
        string name = caption ?? string.Empty;

        // Software-emulated virtual ports (com0com, Eltima, J-Link JTAG serial, …) hang off ROOT.
        if (id.StartsWith("ROOT\\", StringComparison.OrdinalIgnoreCase)
            || name.Contains("com0com", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
            || name.Contains("vsp", StringComparison.OrdinalIgnoreCase))
        {
            return DiscoveredPortType.Virtual;
        }

        if (id.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase)
            || id.StartsWith("USBSTOR\\", StringComparison.OrdinalIgnoreCase))
        {
            return DiscoveredPortType.UsbSerial;
        }

        return DiscoveredPortType.Serial;
    }

    private static string ExtractVidPid(string? pnp)
    {
        if (string.IsNullOrEmpty(pnp))
        {
            return string.Empty;
        }

        Match match = Regex.Match(pnp, @"VID_([0-9A-Fa-f]+)&PID_([0-9A-Fa-f]+)");
        return match.Success
            ? $"{match.Groups[1].Value.ToUpperInvariant()} / {match.Groups[2].Value.ToUpperInvariant()}"
            : string.Empty;
    }

    private static string ExtractInstanceId(string? pnp)
    {
        if (string.IsNullOrEmpty(pnp))
        {
            return string.Empty;
        }

        int index = pnp.LastIndexOf('\\');
        return index >= 0 && index < pnp.Length - 1 ? pnp[(index + 1)..] : string.Empty;
    }

    private static string ReadLocationInfo(string? pnp)
    {
        if (string.IsNullOrEmpty(pnp))
        {
            return string.Empty;
        }

        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\" + pnp);
            return key?.GetValue("LocationInformation")?.ToString() ?? string.Empty;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return string.Empty;
        }
    }

    private readonly record struct PnPDeviceInfo(string Caption, string Manufacturer, string PnpDeviceId);
}
