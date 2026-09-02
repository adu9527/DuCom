namespace DuCom.Core.Ports;

public enum PortLifecycleState
{
    Closed,
    Opening,
    Open,
    Closing,
}

public enum PortCommandResult
{
    Succeeded,
    AlreadyOpen,
    AlreadyClosed,
    Cancelled,
    Faulted,
    Disposed,
}

public sealed record PortLifecycleSnapshot(
    string PortName,
    PortLifecycleState State,
    long Version,
    DateTimeOffset ChangedAtUtc,
    string? FaultMessage);

public sealed class TransportDisconnectedEventArgs(Exception exception) : EventArgs
{
    public Exception Exception { get; } = exception ?? throw new ArgumentNullException(nameof(exception));
}

public interface IPortLifecycleTransport : IAsyncDisposable
{
    event EventHandler<TransportDisconnectedEventArgs>? Disconnected;

    ValueTask OpenAsync(CancellationToken cancellationToken);

    ValueTask CloseAsync(CancellationToken cancellationToken);
}

public sealed class PortLifecycle : IAsyncDisposable
{
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly object _lifetimeGate = new();
    private readonly object _snapshotGate = new();
    private readonly IPortLifecycleTransport _transport;
    private PortLifecycleSnapshot _snapshot;
    private Task? _disposeTask;
    private int _disposed;
    private int _transportDisposed;
    private long _disconnectVersion;

    public PortLifecycle(string portName, IPortLifecycleTransport transport)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(portName);
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _snapshot = new PortLifecycleSnapshot(portName, PortLifecycleState.Closed, 0, DateTimeOffset.UtcNow, null);
        _transport.Disconnected += OnTransportDisconnected;
    }

    public event EventHandler<PortLifecycleSnapshot>? StateChanged;

    public PortLifecycleSnapshot Snapshot => Volatile.Read(ref _snapshot);

    public Task<PortCommandResult> OpenAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync(open: true, cancellationToken);

    public Task<PortCommandResult> CloseAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync(open: false, cancellationToken);

    public Task<PortCommandResult> ShutdownAsync(CancellationToken cancellationToken = default) =>
        CloseAsync(cancellationToken);

    public ValueTask DisposeAsync()
    {
        lock (_lifetimeGate)
        {
            if (_disposeTask is null)
            {
                Interlocked.Exchange(ref _disposed, 1);
                _transport.Disconnected -= OnTransportDisconnected;
                _disposeTask = DisposeCoreAsync();
            }

            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        await CloseCoreForDisposeAsync().ConfigureAwait(false);
        await DisposeTransportOnceAsync().ConfigureAwait(false);
        _operationLock.Dispose();
    }

    private async Task<PortCommandResult> ExecuteAsync(bool open, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return PortCommandResult.Disposed;
        }

        try
        {
            await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return PortCommandResult.Cancelled;
        }

        try
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return PortCommandResult.Disposed;
            }

            return open
                ? await OpenCoreAsync(cancellationToken).ConfigureAwait(false)
                : await CloseCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private async Task<PortCommandResult> OpenCoreAsync(CancellationToken cancellationToken)
    {
        if (Snapshot.State is PortLifecycleState.Open or PortLifecycleState.Opening)
        {
            return PortCommandResult.AlreadyOpen;
        }

        Publish(PortLifecycleState.Opening, null);
        long disconnectVersion = Interlocked.Read(ref _disconnectVersion);
        try
        {
            await _transport.OpenAsync(cancellationToken).ConfigureAwait(false);
            if (Interlocked.Read(ref _disconnectVersion) != disconnectVersion)
            {
                return PortCommandResult.Faulted;
            }

            Publish(PortLifecycleState.Open, null);
            return PortCommandResult.Succeeded;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Publish(PortLifecycleState.Closed, null);
            return PortCommandResult.Cancelled;
        }
        catch (Exception exception)
        {
            Publish(PortLifecycleState.Closed, exception.Message);
            return PortCommandResult.Faulted;
        }
    }

    private async Task<PortCommandResult> CloseCoreAsync(CancellationToken cancellationToken)
    {
        if (Snapshot.State is PortLifecycleState.Closed or PortLifecycleState.Closing)
        {
            return PortCommandResult.AlreadyClosed;
        }

        Publish(PortLifecycleState.Closing, null);
        try
        {
            await _transport.CloseAsync(cancellationToken).ConfigureAwait(false);
            Publish(PortLifecycleState.Closed, null);
            return PortCommandResult.Succeeded;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Publish(PortLifecycleState.Open, null);
            return PortCommandResult.Cancelled;
        }
        catch (Exception exception)
        {
            Publish(PortLifecycleState.Closed, exception.Message);
            return PortCommandResult.Faulted;
        }
    }

    private async Task CloseCoreForDisposeAsync()
    {
        await _operationLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Snapshot.State != PortLifecycleState.Closed)
            {
                Publish(PortLifecycleState.Closing, null);
                try
                {
                    await _transport.CloseAsync(CancellationToken.None).ConfigureAwait(false);
                    Publish(PortLifecycleState.Closed, null);
                }
                catch (Exception exception)
                {
                    Publish(PortLifecycleState.Closed, exception.Message);
                }
            }
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private async ValueTask DisposeTransportOnceAsync()
    {
        if (Interlocked.Exchange(ref _transportDisposed, 1) == 0)
        {
            await _transport.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void OnTransportDisconnected(object? sender, TransportDisconnectedEventArgs e)
    {
        Interlocked.Increment(ref _disconnectVersion);
        PortLifecycleSnapshot current = Snapshot;
        if (current.State is PortLifecycleState.Open or PortLifecycleState.Opening)
        {
            Publish(PortLifecycleState.Closed, e.Exception.Message);
        }
    }

    private void Publish(PortLifecycleState state, string? faultMessage)
    {
        PortLifecycleSnapshot next;
        lock (_snapshotGate)
        {
            PortLifecycleSnapshot current = Snapshot;
            next = current with
            {
                State = state,
                Version = current.Version + 1,
                ChangedAtUtc = DateTimeOffset.UtcNow,
                FaultMessage = faultMessage,
            };
            Volatile.Write(ref _snapshot, next);
        }

        StateChanged?.Invoke(this, next);
    }
}
