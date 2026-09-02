using DuCom.Core.Ports;

namespace DuCom.Core.Tests.Ports;

public sealed class PortLifecycleTests
{
    [Fact]
    public async Task OpenAndCloseFollowRequiredTransitions()
    {
        FakeLifecycleTransport transport = new();
        await using PortLifecycle lifecycle = new("COM3", transport);
        List<PortLifecycleState> states = [];
        lifecycle.StateChanged += (_, snapshot) => states.Add(snapshot.State);

        PortCommandResult opened = await lifecycle.OpenAsync();
        PortCommandResult closed = await lifecycle.CloseAsync();

        Assert.Equal(PortCommandResult.Succeeded, opened);
        Assert.Equal(PortCommandResult.Succeeded, closed);
        Assert.Equal([PortLifecycleState.Opening, PortLifecycleState.Open, PortLifecycleState.Closing, PortLifecycleState.Closed], states);
        Assert.Equal(1, transport.OpenCount);
        Assert.Equal(1, transport.CloseCount);
    }

    [Fact]
    public async Task DuplicateCommandsAreIdempotent()
    {
        FakeLifecycleTransport transport = new();
        await using PortLifecycle lifecycle = new("COM3", transport);

        Assert.Equal(PortCommandResult.AlreadyOpen, await OpenTwiceAsync(lifecycle));
        Assert.Equal(PortCommandResult.Succeeded, await lifecycle.CloseAsync());
        Assert.Equal(PortCommandResult.AlreadyClosed, await lifecycle.CloseAsync());
        Assert.Equal(1, transport.OpenCount);
        Assert.Equal(1, transport.CloseCount);
    }

    [Fact]
    public async Task ConcurrentOpenCommandsAreSerialized()
    {
        TaskCompletionSource openEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseOpen = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeLifecycleTransport transport = new(openEntered, releaseOpen);
        await using PortLifecycle lifecycle = new("COM3", transport);

        Task<PortCommandResult> first = lifecycle.OpenAsync();
        await openEntered.Task;
        Task<PortCommandResult> second = lifecycle.OpenAsync();
        releaseOpen.SetResult();

        Assert.Equal(PortCommandResult.Succeeded, await first);
        Assert.Equal(PortCommandResult.AlreadyOpen, await second);
        Assert.Equal(1, transport.OpenCount);
    }

    [Fact]
    public async Task OpenFailureReturnsToClosedWithFaultSnapshot()
    {
        FakeLifecycleTransport transport = new(openException: new IOException("denied"));
        await using PortLifecycle lifecycle = new("COM3", transport);

        PortCommandResult result = await lifecycle.OpenAsync();

        Assert.Equal(PortCommandResult.Faulted, result);
        Assert.Equal(PortLifecycleState.Closed, lifecycle.Snapshot.State);
        Assert.Equal("denied", lifecycle.Snapshot.FaultMessage);
        Assert.DoesNotContain(nameof(IOException), lifecycle.Snapshot.FaultMessage);
    }

    [Fact]
    public async Task CancellationDuringOpenReturnsToClosed()
    {
        FakeLifecycleTransport transport = new(openDelay: TimeSpan.FromSeconds(5));
        await using PortLifecycle lifecycle = new("COM3", transport);
        using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(50));

        PortCommandResult result = await lifecycle.OpenAsync(cancellation.Token);

        Assert.Equal(PortCommandResult.Cancelled, result);
        Assert.Equal(PortLifecycleState.Closed, lifecycle.Snapshot.State);
        Assert.Null(lifecycle.Snapshot.FaultMessage);
    }

    [Fact]
    public async Task CloseFailureReturnsToClosedAndExposesFault()
    {
        FakeLifecycleTransport transport = new(closeException: new IOException("close failed"));
        await using PortLifecycle lifecycle = new("COM3", transport);
        await lifecycle.OpenAsync();

        PortCommandResult result = await lifecycle.CloseAsync();

        Assert.Equal(PortCommandResult.Faulted, result);
        Assert.Equal(PortLifecycleState.Closed, lifecycle.Snapshot.State);
        Assert.Contains("close failed", lifecycle.Snapshot.FaultMessage);
    }

    [Fact]
    public async Task ConcurrentDisposeCallsWaitForTheSameCloseAndTransportDisposal()
    {
        TaskCompletionSource closeEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseClose = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeLifecycleTransport transport = new(closeEntered: closeEntered, releaseClose: releaseClose);
        PortLifecycle lifecycle = new("COM3", transport);
        await lifecycle.OpenAsync();

        Task first = lifecycle.DisposeAsync().AsTask();
        await closeEntered.Task;
        Task second = lifecycle.DisposeAsync().AsTask();

        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        releaseClose.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(1, transport.CloseCount);
        Assert.Equal(1, transport.DisposeCount);
    }

    [Fact]
    public async Task OpenFailureDoesNotExposeExceptionStackTrace()
    {
        ArgumentException failure = new("The given port name does not resolve to a valid serial port", "portName");
        FakeLifecycleTransport transport = new(openException: failure);
        await using PortLifecycle lifecycle = new("COM39", transport);

        await lifecycle.OpenAsync();

        Assert.Equal(failure.Message, lifecycle.Snapshot.FaultMessage);
        Assert.DoesNotContain("System.ArgumentException", lifecycle.Snapshot.FaultMessage);
        Assert.DoesNotContain(" at ", lifecycle.Snapshot.FaultMessage);
    }

    [Fact]
    public async Task TransportDisconnectMovesOpenLifecycleToClosed()
    {
        FakeLifecycleTransport transport = new();
        await using PortLifecycle lifecycle = new("COM3", transport);
        await lifecycle.OpenAsync();

        transport.RaiseDisconnected(new IOException("unplugged"));

        Assert.Equal(PortLifecycleState.Closed, lifecycle.Snapshot.State);
        Assert.Contains("unplugged", lifecycle.Snapshot.FaultMessage);
    }

    [Fact]
    public async Task DisconnectDuringOpeningCannotBeOverwrittenByOpenCompletion()
    {
        TaskCompletionSource openEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseOpen = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeLifecycleTransport transport = new(openEntered, releaseOpen);
        await using PortLifecycle lifecycle = new("COM3", transport);

        Task<PortCommandResult> open = lifecycle.OpenAsync();
        await openEntered.Task;
        transport.RaiseDisconnected(new IOException("unplugged"));
        releaseOpen.SetResult();

        Assert.Equal(PortCommandResult.Faulted, await open);
        Assert.Equal(PortLifecycleState.Closed, lifecycle.Snapshot.State);
        Assert.Contains("unplugged", lifecycle.Snapshot.FaultMessage);
    }

    [Fact]
    public async Task ShutdownAndDisposeAreIdempotent()
    {
        FakeLifecycleTransport transport = new();
        PortLifecycle lifecycle = new("COM3", transport);
        await lifecycle.OpenAsync();

        Assert.Equal(PortCommandResult.Succeeded, await lifecycle.ShutdownAsync());
        Assert.Equal(PortCommandResult.AlreadyClosed, await lifecycle.ShutdownAsync());
        await lifecycle.DisposeAsync();
        await lifecycle.DisposeAsync();

        Assert.Equal(1, transport.CloseCount);
        Assert.Equal(1, transport.DisposeCount);
        Assert.Equal(PortCommandResult.Disposed, await lifecycle.OpenAsync());
    }

    private static async Task<PortCommandResult> OpenTwiceAsync(PortLifecycle lifecycle)
    {
        Assert.Equal(PortCommandResult.Succeeded, await lifecycle.OpenAsync());
        return await lifecycle.OpenAsync();
    }

    private sealed class FakeLifecycleTransport(
        TaskCompletionSource? openEntered = null,
        TaskCompletionSource? releaseOpen = null,
        TaskCompletionSource? closeEntered = null,
        TaskCompletionSource? releaseClose = null,
        Exception? openException = null,
        Exception? closeException = null,
        TimeSpan openDelay = default) : IPortLifecycleTransport
    {
        public event EventHandler<TransportDisconnectedEventArgs>? Disconnected;

        public int OpenCount { get; private set; }

        public int CloseCount { get; private set; }

        public int DisposeCount { get; private set; }

        public async ValueTask OpenAsync(CancellationToken cancellationToken)
        {
            OpenCount++;
            openEntered?.SetResult();
            if (releaseOpen is not null)
            {
                await releaseOpen.Task.WaitAsync(cancellationToken);
            }

            if (openDelay > TimeSpan.Zero)
            {
                await Task.Delay(openDelay, cancellationToken);
            }

            if (openException is not null)
            {
                throw openException;
            }
        }

        public async ValueTask CloseAsync(CancellationToken cancellationToken)
        {
            CloseCount++;
            closeEntered?.TrySetResult();
            if (releaseClose is not null)
            {
                await releaseClose.Task.WaitAsync(cancellationToken);
            }

            if (closeException is not null)
            {
                throw closeException;
            }
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }

        public void RaiseDisconnected(Exception exception) =>
            Disconnected?.Invoke(this, new TransportDisconnectedEventArgs(exception));
    }
}
