using CommunityToolkit.Mvvm.Input;
using DuCom.Core.Ports;
using DuCom.Services;

namespace DuCom.ViewModels;

public partial class MainViewModel
{
    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task RefreshPortsAsync() => _portRefreshCoordinator.RequestRefreshAsync();

    private Task<PortDiscoverySnapshot> DiscoverPortsAsync()
    {
        return Task.Run(() =>
        {
            IReadOnlyDictionary<string, DiscoveredPort> details =
                (_portDiscovery as IPortDetailsProvider)?.GetPortDetails()
                ?? new Dictionary<string, DiscoveredPort>(StringComparer.OrdinalIgnoreCase);
            string[] names = details.Count > 0
                ? [.. details.Keys]
                : [.. _portDiscovery.GetPortNames()];
            return new PortDiscoverySnapshot(names, details);
        });
    }

    private void ApplyDiscoveredPorts(PortDiscoverySnapshot discovered)
    {
        string? previous = SelectedPort;
        _discoveredPortDetails = discovered.Details;
        _discoveredPortNames = discovered.Names;
        RebuildPortItems(previous);
        Workspace.HandleDiscoveredPorts(_discoveredPortNames);
    }

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
            : AvailablePorts.FirstOrDefault();
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
