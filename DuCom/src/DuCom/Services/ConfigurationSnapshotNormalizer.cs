using DuCom.Core.Persistence;

namespace DuCom.Services;

internal static class ConfigurationSnapshotNormalizer
{
    internal static ConfigurationApplyPlan Normalize(
        ConfigurationSnapshot snapshot,
        IReadOnlyList<string> timestampFormats,
        IReadOnlyList<string> logFontFamilies)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(timestampFormats);
        ArgumentNullException.ThrowIfNull(logFontFamilies);

        int[]? customBaudRates = snapshot.CustomBaudRates is { Count: > 0 }
            ? [.. snapshot.CustomBaudRates.Where(value => value > 0).Distinct().Order()]
            : null;

        return new ConfigurationApplyPlan(
            customBaudRates,
            SettingsCatalog.NormalizeTimestampFormat(snapshot.TimestampFormat, timestampFormats),
            SettingsCatalog.NormalizePrivateMemoryThreshold(snapshot.PrivateMemoryThresholdMiB),
            SettingsCatalog.NormalizeRefreshInterval(snapshot.MemoryRefreshInterval),
            SettingsCatalog.NormalizeRefreshInterval(snapshot.SystemMemoryRefreshInterval),
            SettingsCatalog.NormalizeLogFontSize(snapshot.LogFontSize),
            SettingsCatalog.NormalizeLogFontFamily(snapshot.LogFontFamily, logFontFamilies),
            SettingsCatalog.NormalizeSearchOpacity(snapshot.SearchOpacity),
            SettingsCatalog.NormalizeTelnetPort(snapshot.TelnetPort),
            new Dictionary<string, PortSettingSnapshot>(snapshot.PortOverrides ?? [], StringComparer.OrdinalIgnoreCase),
            [.. snapshot.RightPanePorts ?? []],
            [.. snapshot.SessionOrder ?? []],
            [.. snapshot.OpenSessionPorts ?? []],
            SettingsCatalog.NormalizeSplitterRatio(snapshot.SplitterRatio),
            [.. PortVisibility.NormalizeHidden(snapshot.HiddenPorts)],
            NormalizePortNames(snapshot.CommandTargetPortNames));
    }

    internal static string[] NormalizePortNames(IEnumerable<string>? portNames) => [.. (portNames ?? [])
        .Where(name => !string.IsNullOrWhiteSpace(name))
        .Select(name => name.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
        .ThenBy(name => name, StringComparer.Ordinal)];
}

internal sealed record ConfigurationApplyPlan(
    IReadOnlyList<int>? CustomBaudRates,
    string TimestampFormat,
    int PrivateMemoryThresholdMiB,
    int MemoryRefreshInterval,
    int SystemMemoryRefreshInterval,
    double LogFontSize,
    string LogFontFamily,
    double SearchOpacity,
    int TelnetPort,
    Dictionary<string, PortSettingSnapshot> PortOverrides,
    List<string> RightPanePorts,
    List<string> SessionOrder,
    List<string> OpenSessionPorts,
    double SplitterRatio,
    IReadOnlyList<string> HiddenPorts,
    string[] CommandTargetPortNames);
