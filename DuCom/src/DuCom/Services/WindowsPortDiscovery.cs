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
    Unknown,
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
    // This filtered metadata query can still be slow; it runs independently of presence.
    private const string PortInfoSelectSql =
        "SELECT Caption, Name, Manufacturer, PNPDeviceID, DeviceID FROM Win32_PnPEntity WHERE Caption LIKE '%(COM%'";

    private static readonly Regex ParenthesisedComPort = new(
        @"\(\s*(COM\d+)\s*\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex BareComPort = new(
        @"\b(COM\d+)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly Func<IReadOnlyCollection<string>?> _readPresentNames;
    private readonly Func<string[]> _readRuntimeNames;
    private readonly PortMetadataCache _metadata;
    private readonly Func<IReadOnlyDictionary<string, DiscoveredPort>?>? _readPresentDetails;

    public WindowsPortDiscovery() : this(
        WindowsPresentPortEnumerator.GetPortNames,
        ReadRuntimePortNames,
        new PortMetadataCache(() => Task.Run(QueryPortMetadata), () => DateTimeOffset.UtcNow,
            exception => Program.DiagnosticLog?.Warning("Serial-port metadata refresh failed.", exception)),
        WindowsPresentPortEnumerator.GetPortDetails)
    {
    }

    internal WindowsPortDiscovery(
        Func<IReadOnlyCollection<string>?> readPresentNames,
        Func<string[]> readRuntimeNames,
        PortMetadataCache metadata,
        Func<IReadOnlyDictionary<string, DiscoveredPort>?>? readPresentDetails = null)
    {
        _readPresentDetails = readPresentDetails;
        _readPresentNames = readPresentNames;
        _readRuntimeNames = readRuntimeNames;
        _metadata = metadata;
    }

    public IReadOnlyList<string> GetPortNames()
    {
        IReadOnlyCollection<string>? present = _readPresentNames();
        return SelectPortNames(present, present is null ? _readRuntimeNames() : []);
    }

    public IReadOnlyDictionary<string, DiscoveredPort> GetPortDetails()
    {
        // Presence is authoritative and cheap; a hung/slow WMI provider cannot delay it.
        IReadOnlyDictionary<string, DiscoveredPort>? native = _readPresentDetails?.Invoke();
        IReadOnlyList<string> names = _readPresentDetails is null ? GetPortNames()
            : SelectPortNames(native?.Keys.ToArray(), native is null ? _readRuntimeNames() : []);
        IReadOnlyDictionary<string, DiscoveredPort> metadata = _metadata.Get(names);
        Dictionary<string, DiscoveredPort> ports = new(StringComparer.OrdinalIgnoreCase);
        foreach (string portName in names)
        {
            ports[portName] = native is not null && native.TryGetValue(portName, out DiscoveredPort? presentDetail)
                ? presentDetail : new DiscoveredPort(
                portName,
                DiscoveredPortType.Unknown,
                string.Empty,
                portName,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty);
        }

        foreach ((string portName, DiscoveredPort detail) in metadata)
        {
            if (ports.TryGetValue(portName, out DiscoveredPort? present))
            {
                // Native identity/classification belongs to this devnode generation. A
                // cached WMI caption must not reclassify a reused COM number.
                if (!string.IsNullOrEmpty(present.DeviceInstanceId)
                    && !string.Equals(present.DeviceInstanceId, detail.DeviceInstanceId, StringComparison.OrdinalIgnoreCase)) continue;
                ports[portName] = present.Type == DiscoveredPortType.Unknown ? detail
                    : detail with { Type = present.Type }; // Enrich, never resurrect or reclassify.
            }
        }

        return ports;
    }

    private static IReadOnlyDictionary<string, DiscoveredPort> QueryPortMetadata()
    {
        Dictionary<string, DiscoveredPort> ports = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string portName, PnPDeviceInfo info) in QueryPnPDevices())
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

    internal static string[] SelectPortNames(
        IReadOnlyCollection<string>? presentPorts,
        IEnumerable<string> runtimePorts,
        IEnumerable<string>? metadataPorts = null) =>
        // WMI is not a presence fallback either: ghost captions can survive removals.
        (presentPorts is not null ? presentPorts : runtimePorts)
            .Where(Com0ComService.IsValidPortName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string[] ReadRuntimePortNames()
    {
        try
        {
            return SerialPort.GetPortNames();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Program.DiagnosticLog?.Warning("Failed to read fallback serial-port mappings.", exception);
            return [];
        }
    }

    private static Dictionary<string, PnPDeviceInfo> QueryPnPDevices()
    {
        Dictionary<string, PnPDeviceInfo> byPort = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var searcher = new ManagementObjectSearcher(PortInfoSelectSql);
            using ManagementObjectCollection results = searcher.Get();
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

    internal static DiscoveredPortType GetPortType(string? pnpDeviceId, string? caption)
    {
        string id = pnpDeviceId ?? string.Empty;
        string name = caption ?? string.Empty;

        // Bluetooth SPP and software-emulated ports have no directly attached serial hardware.
        if (id.StartsWith("ROOT\\", StringComparison.OrdinalIgnoreCase)
            || id.StartsWith("BTHENUM\\", StringComparison.OrdinalIgnoreCase)
            || id.StartsWith("BTHMODEM\\", StringComparison.OrdinalIgnoreCase)
            || id.StartsWith("BTHPORT\\", StringComparison.OrdinalIgnoreCase)
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

        return string.IsNullOrWhiteSpace(id) ? DiscoveredPortType.Unknown : DiscoveredPortType.Serial;
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
