using System.Diagnostics;
using CommunityToolkit.Mvvm.Input;
using DuCom.Core.Ports;
using DuCom.Services;

namespace DuCom.ViewModels;

public partial class MainViewModel
{
    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task RefreshPortsAsync()
    {
        lock (_portRefreshSync)
        {
            _portRefreshRequested = true;
            return _portRefreshTask ??= RunPortRefreshLoopAsync();
        }
    }

    private async Task RunPortRefreshLoopAsync()
    {
        while (!_disposed)
        {
            lock (_portRefreshSync)
            {
                _portRefreshRequested = false;
            }

            Stopwatch stopwatch = Stopwatch.StartNew();
            PortDiscoverySnapshot discovered;
            try
            {
                discovered = await Task.Run(() =>
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
            catch (Exception exception)
            {
                Program.DiagnosticLog?.Warning("Serial-port discovery failed.", exception);
                lock (_portRefreshSync)
                {
                    _portRefreshTask = null;
                }
                return;
            }

            stopwatch.Stop();
            if (stopwatch.Elapsed >= SlowOperationThreshold)
            {
                Program.DiagnosticLog?.Warning(
                    $"Slow serial-port discovery. ElapsedMs={stopwatch.Elapsed.TotalMilliseconds:0.0}; Ports={discovered.Names.Length}");
            }

            lock (_portRefreshSync)
            {
                if (_portRefreshRequested)
                {
                    continue;
                }
            }

            if (_disposed)
            {
                return;
            }

            string? previous = SelectedPort;
            _discoveredPortDetails = discovered.Details;
            _discoveredPortNames = discovered.Names;
            RebuildPortItems(previous);
            CloseSessionsForRemovedPorts();
            ReconnectReturnedPorts();

            lock (_portRefreshSync)
            {
                if (_portRefreshRequested)
                {
                    continue;
                }

                _portRefreshTask = null;
                return;
            }
        }

        lock (_portRefreshSync)
        {
            _portRefreshTask = null;
        }
    }

    private void CloseSessionsForRemovedPorts()
    {
        HashSet<string> discovered = new(_discoveredPortNames, StringComparer.OrdinalIgnoreCase);
        foreach (SessionViewModel session in Sessions.Where(session => session.IsOpen && !discovered.Contains(session.PortName)).ToArray())
        {
            session.MarkDeviceRemoved();
            _ = CloseRemovedPortSessionAsync(session);
        }
    }

    private void ReconnectReturnedPorts()
    {
        HashSet<string> discovered = new(_discoveredPortNames, StringComparer.OrdinalIgnoreCase);
        foreach (SessionViewModel session in Sessions.Where(session => session.IsWaitingForReconnect && discovered.Contains(session.PortName)).ToArray())
        {
            _ = ReconnectReturnedPortAsync(session);
        }
    }

    private async Task ReconnectReturnedPortAsync(SessionViewModel session)
    {
        if (!session.IsWaitingForReconnect || session.IsBusy || session.IsOpen)
        {
            return;
        }

        session.ClearReconnectWait();
        try
        {
            SessionViewModel replacement = await RebuildClosedSessionAsync(session);
            replacement.AutoReconnect = true;
            PortCommandResult result = await replacement.OpenAsync();
            Program.DiagnosticLog?.Information($"Automatic reconnect completed. Port={replacement.PortName}; Result={result}");
        }
        catch (Exception exception)
        {
            Program.DiagnosticLog?.Warning($"Automatic reconnect failed. Port={session.PortName}.", exception);
        }
    }

    private static async Task CloseRemovedPortSessionAsync(SessionViewModel session)
    {
        try
        {
            Program.DiagnosticLog?.Warning($"Connected serial port disappeared from discovery; closing its session. Port={session.PortName}");
            await session.CloseAsync();
        }
        catch (Exception exception)
        {
            Program.DiagnosticLog?.Error($"Failed to close removed serial-port session. Port={session.PortName}", exception);
        }
    }

    private void RebuildPortItems(string? selectedPort = null)
    {
        IEnumerable<string> names = PortSortMode switch
        {
            PortSortMode.NameDescending => _discoveredPortNames.OrderByDescending(name => name, StringComparer.OrdinalIgnoreCase),
            PortSortMode.ConnectedFirst => _discoveredPortNames
                .OrderByDescending(name => Sessions.Any(session => session.IsOpen && string.Equals(session.PortName, name, StringComparison.OrdinalIgnoreCase)))
                .ThenBy(name => name, StringComparer.OrdinalIgnoreCase),
            _ => _discoveredPortNames.Order(StringComparer.OrdinalIgnoreCase),
        };
        AvailablePorts.Clear();
        foreach (string name in names)
        {
            bool hidden = _hiddenPorts.Contains(name);
            _discoveredPortDetails.TryGetValue(name, out DiscoveredPort? detail);
            bool isVirtual = detail?.Type == DiscoveredPortType.Virtual;
            bool typeVisible = isVirtual ? ShowVirtualPorts : ShowSerialPorts;
            if (typeVisible && (!hidden || ShowHiddenPorts))
            {
                string type = detail?.Type switch
                {
                    DiscoveredPortType.Virtual => "VAR",
                    DiscoveredPortType.UsbSerial => "USB",
                    _ => "COM",
                };
                AvailablePorts.Add(new PortItemViewModel(
                    name,
                    TogglePortAsync,
                    TogglePortHidden,
                    type,
                    detail?.Description ?? string.Empty,
                    detail?.DeviceName ?? name,
                    detail?.Manufacturer ?? string.Empty,
                    detail?.VidPid ?? string.Empty,
                    detail?.SerialNumber ?? string.Empty,
                    detail?.DeviceInstanceId ?? string.Empty,
                    detail?.LocationInfo ?? string.Empty)
                {
                    IsHidden = hidden,
                });
            }
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

    private sealed record PortDiscoverySnapshot(
        string[] Names,
        IReadOnlyDictionary<string, DiscoveredPort> Details);
}
