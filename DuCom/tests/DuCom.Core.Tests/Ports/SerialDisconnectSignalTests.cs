using DuCom.Core.Ports;

namespace DuCom.Core.Tests.Ports;

public sealed class SerialDisconnectSignalTests
{
    [Fact]
    public void UnrecoverableOpenStateFailureIsReportedOnlyOnce()
    {
        SerialDisconnectSignal signal = new();
        signal.MarkOpened();

        Assert.True(signal.TryReport(new IOException("unplugged"), isOpen: false, closeRequested: false, disposed: false));
        Assert.False(signal.TryReport(new IOException("still unplugged"), isOpen: false, closeRequested: false, disposed: false));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void NormalCloseOrDisposeSuppressesDisconnect(bool closeRequested, bool disposed)
    {
        SerialDisconnectSignal signal = new();
        signal.MarkOpened();

        Assert.False(signal.TryReport(new IOException("closed"), isOpen: false, closeRequested, disposed));
    }

    [Fact]
    public void RecoverableOperationErrorDoesNotSignalDisconnect()
    {
        SerialDisconnectSignal signal = new();
        signal.MarkOpened();

        Assert.False(signal.TryReport(new TimeoutException("timeout"), isOpen: true, closeRequested: false, disposed: false));
    }

    [Fact]
    public void SuccessfulReopenArmsOneNewDisconnectSignal()
    {
        SerialDisconnectSignal signal = new();
        signal.MarkOpened();
        Assert.True(signal.TryReport(new UnauthorizedAccessException("removed"), isOpen: false, closeRequested: false, disposed: false));

        signal.MarkOpened();

        Assert.True(signal.TryReport(new InvalidOperationException("closed handle"), isOpen: false, closeRequested: false, disposed: false));
    }
}
