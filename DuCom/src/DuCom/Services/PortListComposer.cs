using DuCom.ViewModels;

namespace DuCom.Services;

/// <summary>One user-visible port entry produced by <see cref="PortListComposer.Compose"/>.</summary>
internal sealed record ComposedPort(
    string PortName,
    string TypeLabel,
    bool IsHidden,
    DiscoveredPort? Detail);

/// <summary>
/// Pure port-list assembly: ordering, visibility filtering, and type labelling for the
/// port picker. Extracted from MainViewModel so the rules are unit-testable without a
/// Dispatcher; the view model only applies the result to its observable collection.
/// Internal because it exposes the internal DiscoveredPort details type.
/// </summary>
internal static class PortListComposer
{
    public static IReadOnlyList<ComposedPort> Compose(
        IReadOnlyList<string> discoveredNames,
        IReadOnlyDictionary<string, DiscoveredPort> details,
        IReadOnlyCollection<string> hiddenPorts,
        PortSortMode sortMode,
        bool showSerialPorts,
        bool showVirtualPorts,
        bool showHiddenPorts,
        Func<string, bool> isPortOpen)
    {
        ArgumentNullException.ThrowIfNull(discoveredNames);
        ArgumentNullException.ThrowIfNull(details);
        ArgumentNullException.ThrowIfNull(hiddenPorts);
        ArgumentNullException.ThrowIfNull(isPortOpen);

        IEnumerable<string> ordered = sortMode switch
        {
            PortSortMode.NameDescending => discoveredNames.OrderByDescending(name => name, StringComparer.OrdinalIgnoreCase),
            PortSortMode.ConnectedFirst => discoveredNames
                .OrderByDescending(isPortOpen)
                .ThenBy(name => name, StringComparer.OrdinalIgnoreCase),
            _ => discoveredNames.Order(StringComparer.OrdinalIgnoreCase),
        };

        List<ComposedPort> result = [];
        foreach (string name in ordered)
        {
            bool hidden = hiddenPorts.Contains(name);
            details.TryGetValue(name, out DiscoveredPort? detail);
            bool isVirtual = detail?.Type == DiscoveredPortType.Virtual;
            bool typeVisible = isVirtual ? showVirtualPorts : showSerialPorts;
            if (!typeVisible || (hidden && !showHiddenPorts))
            {
                continue;
            }

            string type = detail?.Type switch
            {
                DiscoveredPortType.Virtual => "VAR",
                DiscoveredPortType.UsbSerial => "USB",
                _ => "COM",
            };
            result.Add(new ComposedPort(name, type, hidden, detail));
        }

        return result;
    }
}
