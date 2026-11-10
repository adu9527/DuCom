using CommunityToolkit.Mvvm.Input;
using DuCom.Core.Ports;
using DuCom.Services;

namespace DuCom.ViewModels;

public partial class MainViewModel
{
    private bool _rebuildingPortItems;
    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task RefreshPortsAsync() => _portRefreshCoordinator.RequestRefreshAsync();

    private Task<PortDiscoverySnapshot> DiscoverPortsAsync() => Task.Run(() => DiscoverPorts(_portDiscovery));

    internal static PortDiscoverySnapshot DiscoverPorts(IPortDiscovery discovery)
    {
        if (discovery is IPortDetailsProvider provider)
        {
            // An empty details snapshot is a valid 'all devices removed' result, not
            // a reason to enumerate again and combine two different moments in time.
            IReadOnlyDictionary<string, DiscoveredPort> details = provider.GetPortDetails();
            return new PortDiscoverySnapshot([.. details.Keys], details);
        }

        return new PortDiscoverySnapshot([.. discovery.GetPortNames()],
            new Dictionary<string, DiscoveredPort>(StringComparer.OrdinalIgnoreCase));
    }

    private void ApplyDiscoveredPorts(PortDiscoverySnapshot discovered)
    {
        // The watchdog usually observes no change. Avoid clearing the bound list
        // (and its selection) every two seconds just because a new snapshot exists.
        bool changed = !SameDiscoveredPorts(
            new PortDiscoverySnapshot(_discoveredPortNames, _discoveredPortDetails), discovered);
        if (!changed && PortSortMode == PortSortMode.ConnectedFirst)
        {
            changed = !AvailablePorts.Select(item => item.PortName).SequenceEqual(
                AvailablePorts.OrderByDescending(item => Workspace.Sessions.Any(session => session.IsOpen
                    && string.Equals(session.PortName, item.PortName, StringComparison.OrdinalIgnoreCase)))
                    .ThenBy(item => item.PortName, StringComparer.OrdinalIgnoreCase).Select(item => item.PortName),
                StringComparer.OrdinalIgnoreCase);
        }
        string? previous = SelectedPort;
        _discoveredPortDetails = discovered.Details;
        _discoveredPortNames = discovered.Names;
        if (changed)
        {
            RebuildPortItems(previous);
        }
        Workspace.HandleDiscoveredPorts(_discoveredPortNames);
    }

    internal static bool SameDiscoveredPorts(PortDiscoverySnapshot previous, PortDiscoverySnapshot current) =>
        previous.Names.SequenceEqual(current.Names, StringComparer.OrdinalIgnoreCase)
        && previous.Details.Count == current.Details.Count
        && previous.Details.All(pair => current.Details.TryGetValue(pair.Key, out DiscoveredPort? detail) && pair.Value == detail);

    private void RebuildPortItems(string? selectedPort = null)
    {
        IReadOnlyList<ComposedPort> composed = PortListComposer.Compose(
            _discoveredPortNames,
            _discoveredPortDetails,
            _hiddenPorts,
            PortSortMode,
            ShowSerialPorts,
            ShowVirtualPorts,
            ShowHiddenPorts,
            isPortOpen: name => Workspace.Sessions.Any(session =>
                session.IsOpen && string.Equals(session.PortName, name, StringComparison.OrdinalIgnoreCase)));
        _rebuildingPortItems = true;
        try
        {
            AvailablePorts.Clear();
            foreach (ComposedPort port in composed)
            {
                AvailablePorts.Add(new PortItemViewModel(
                    port.PortName,
                    port => Workspace.TogglePortAsync(port.PortName),
                    TogglePortHidden,
                    port.TypeLabel,
                    port.Detail?.Description ?? string.Empty,
                    port.Detail?.DeviceName ?? port.PortName,
                    port.Detail?.Manufacturer ?? string.Empty,
                    port.Detail?.VidPid ?? string.Empty,
                    port.Detail?.SerialNumber ?? string.Empty,
                    port.Detail?.DeviceInstanceId ?? string.Empty,
                    port.Detail?.LocationInfo ?? string.Empty)
                {
                    IsHidden = port.IsHidden,
                });
            }

            SelectedPortItem = selectedPort is not null
                ? AvailablePorts.FirstOrDefault(item => string.Equals(item.PortName, selectedPort, StringComparison.OrdinalIgnoreCase))
                : null;
        }
        finally { _rebuildingPortItems = false; }
    }

    [RelayCommand]
    private void ShowVisiblePorts()
    {
        ShowHiddenPorts = false;
        RebuildPortItems(SelectedPort);
    }

    [RelayCommand]
    private void ShowAllPorts()
    {
        ShowHiddenPorts = true;
        RebuildPortItems(SelectedPort);
    }

    [RelayCommand]
    private void RestoreAllHiddenPorts()
    {
        _hiddenPorts.Clear();
        ShowHiddenPorts = false;
        MarkSettingsDirty();
        RebuildPortItems(SelectedPort);
    }

    [RelayCommand]
    private void SetPortSort(string mode)
    {
        if (Enum.TryParse(mode, true, out PortSortMode parsed))
        {
            PortSortMode = parsed;
            RebuildPortItems(SelectedPort);
        }
    }

    [RelayCommand]
    private void TogglePortHidden(PortItemViewModel port)
    {
        if (port.IsHidden)
        {
            _hiddenPorts.Remove(port.PortName);
        }
        else
        {
            _hiddenPorts.Add(port.PortName);
        }

        MarkSettingsDirty(); // hidden ports are part of the persisted settings snapshot
        RebuildPortItems(SelectedPort);
    }

}
