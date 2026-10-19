using DuCom.ViewModels;
using Xunit;

namespace DuCom.App.Tests;

public sealed class SerialFaultMessageTests
{
    [Theory]
    [InlineData("Access to the port 'COM3' is denied.", "Status.PortOccupied")]
    [InlineData("System.UnauthorizedAccessException: Access denied.\n   at SerialPort.Open()", "Status.PortOccupied")]
    [InlineData("拒绝访问端口 COM3。", "Status.PortOccupied")]
    [InlineData("The operation has timed out.", "Status.PortTimeout")]
    [InlineData("System.ArgumentOutOfRangeException: baudRate", "Status.InvalidPortSettings")]
    [InlineData("The port 'COM3' does not exist.", "Status.PortUnavailable")]
    [InlineData("COM3 does not resolve to a valid serial port", "Status.PortUnavailable")]
    [InlineData("System.IO.IOException: The I/O operation has been aborted.\n   at Read()", "Status.PortConnectionFault")]
    [InlineData("A device attached to the system is not functioning.", "Status.PortConnectionFault")]
    public void NativeFaultsUseLocalizedResourcesInsteadOfRawExceptions(string fault, string resourceKey)
    {
        // Without a WPF application the resource lookup returns the resource key.
        Assert.Equal(resourceKey, SessionViewModel.GetUserFaultMessage(fault));
    }

    [Fact]
    public void NoFaultHasNoMessage() => Assert.Empty(SessionViewModel.GetUserFaultMessage(string.Empty));
}
