using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DuCom.Core.Ports;

namespace DuCom.ViewModels;

public partial class PortItemViewModel(
    string portName,
    Func<PortItemViewModel, Task> toggleConnection,
    Action<PortItemViewModel> toggleHidden,
    string portType = "COM",
    string description = "",
    string deviceName = "",
    string manufacturer = "",
    string vidPid = "",
    string serialNumber = "",
    string deviceInstanceId = "",
    string locationInfo = "") : ObservableObject
{
    public string PortName { get; } = portName;

    public string PortType { get; } = portType;

    public string Description { get; } = description;

    public string DeviceName { get; } = deviceName;

    public string Manufacturer { get; } = manufacturer;

    public string VidPid { get; } = vidPid;

    public string SerialNumber { get; } = serialNumber;

    public string DeviceInstanceId { get; } = deviceInstanceId;

    public string LocationInfo { get; } = locationInfo;

    // Display variants that fall back to an em dash so the detail tooltip stays aligned
    // even when a device reports no manufacturer / VID-PID / location.
    public string DisplayDeviceName => string.IsNullOrWhiteSpace(DeviceName) ? PortName : DeviceName;

    public string DisplayManufacturer => OrDash(Manufacturer);

    public string DisplayVidPid => OrDash(VidPid);

    public string DisplaySerialNumber => OrDash(SerialNumber);

    public string DisplayDeviceInstanceId => OrDash(DeviceInstanceId);

    public string DisplayLocationInfo => OrDash(LocationInfo);

    private static string OrDash(string? value) => string.IsNullOrWhiteSpace(value) ? "-" : value;

    [ObservableProperty]
    public partial PortLifecycleState State { get; private set; }

    [ObservableProperty]
    public partial bool IsOpen { get; private set; }

    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    public partial bool HasFault { get; private set; }

    [ObservableProperty]
    public partial bool HasWarnings { get; private set; }

    [ObservableProperty]
    public partial bool IsHidden { get; internal set; }

    [RelayCommand]
    private async Task ToggleConnectionAsync()
    {
        IsBusy = true;
        try
        {
            await toggleConnection(this);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void ToggleHidden() => toggleHidden(this);

    internal void Update(SessionViewModel? session)
    {
        State = session?.State ?? PortLifecycleState.Closed;
        IsOpen = session?.IsOpen == true;
        IsBusy = session?.IsBusy == true;
        HasFault = session?.HasFault == true;
        HasWarnings = session?.Warnings.Count > 0;
    }
}
