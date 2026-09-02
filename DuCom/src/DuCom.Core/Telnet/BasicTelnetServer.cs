using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Text;

namespace DuCom.Core.Telnet;

/// <summary>
/// TCP listener for the Telnet bridge. Binds <see cref="System.Net.IPAddress.Loopback"/>
/// by default; remote binding requires the explicit <see cref="TelnetListenOptions.AllowRemote"/>
/// opt-in. Clients receive session log lines pushed from display snapshots; incoming client
/// bytes raise <see cref="ClientDataReceived"/> as raw chunks for the bridge to frame and
/// send through the bound serial session. Every accept/handler task is tracked; a slow
/// client is disconnected after a bounded send wait instead of delaying other clients.
/// </summary>
public sealed class BasicTelnetServer : IAsyncDisposable
{
    private static readonly TimeSpan SlowClientSendTimeout = TimeSpan.FromSeconds(3);

    private readonly ConcurrentDictionary<TcpClient, byte> _clients = new();
    private readonly object _lifecycleGate = new();
    private CancellationTokenSource? _cancellation;
    private TcpListener? _listener;
    private Task? _acceptTask;
    private List<Task> _clientTasks = [];
    private Task? _stopInFlight;
    private Task? _disposeTask;
    private int _disposed;

    public event EventHandler? StatusChanged;

    public event Action<TcpClient, ReadOnlyMemory<byte>>? ClientDataReceived;

    public event Action<TcpClient>? ClientConnected;

    public event Action<TcpClient>? ClientDisconnected;

    public event Action<string>? BridgeDiagnostic;

    public bool IsRunning => _listener is not null;

    public int ClientCount => _clients.Count;

    public string? LocalEndPoint => (_listener?.LocalEndpoint as System.Net.IPEndPoint)?.ToString();

    public IReadOnlyList<string> ClientEndpoints =>
        [.. _clients.Keys.Select(client => client.Client.RemoteEndPoint?.ToString() ?? "unknown")];

    /// <summary>Starts listening on the loopback interface on the given port.</summary>
    public void Start(int port)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65_535);
        Start(new TelnetListenOptions(port, AllowRemote: false));
    }

    /// <summary>Starts listening using the bind policy (loopback unless AllowRemote).</summary>
    public void Start(TelnetListenOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (_stopInFlight is { IsCompleted: false })
            {
                throw new InvalidOperationException("The Telnet listener is still stopping; wait for StopAsync to complete before restarting.");
            }

            // A completed marker from a finished stop is stale; this start replaces it.
            _stopInFlight = null;
            if (_listener is not null)
            {
                return;
            }

            _cancellation = new CancellationTokenSource();
            _listener = new TcpListener(options.BindAddress, options.Port);
            _listener.Start();
            _acceptTask = Task.Run(() => AcceptLoopAsync(_listener, _cancellation.Token));
            BridgeDiagnostic?.Invoke($"Telnet bridge listening on {options.BindAddress}:{options.Port}");
        }

        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Stops the listener. Repeatable: every stop tears the current listener down and a
    /// later <see cref="Start(TelnetListenOptions)"/> may restart it. Concurrent stop
    /// callers join the one in-flight stop. <see cref="DisposeAsync"/> stops once more and
    /// permanently prevents restarts.
    /// </summary>
    public async Task StopAsync()
    {
        Task stopTask;
        lock (_lifecycleGate)
        {
            if (_disposeTask is not null)
            {
                stopTask = _disposeTask;
            }
            else if (_stopInFlight is { IsCompleted: false })
            {
                stopTask = _stopInFlight; // join the stop that is already running
            }
            else if (_listener is null)
            {
                stopTask = Task.CompletedTask; // already stopped: repeated stop is a no-op
            }
            else
            {
                // Note: StopCoreAsync never touches _stopInFlight, so a synchronously
                // completing stop cannot race this assignment.
                stopTask = _stopInFlight = StopCoreAsync();
            }
        }

        await stopTask.ConfigureAwait(false);
    }

    private async Task StopCoreAsync()
    {
        TcpListener? listener;
        CancellationTokenSource? cancellation;
        Task? acceptTask;
        List<Task> clientTasks;
        lock (_lifecycleGate)
        {
            listener = Interlocked.Exchange(ref _listener, null);
            cancellation = Interlocked.Exchange(ref _cancellation, null);
            acceptTask = Interlocked.Exchange(ref _acceptTask, null);
            clientTasks = [.. _clientTasks];
            _clientTasks.Clear();
        }

        cancellation?.Cancel();
        listener?.Stop();
        foreach (TcpClient client in _clients.Keys)
        {
            client.Dispose();
        }

        _clients.Clear();

        if (acceptTask is not null)
        {
            await SafeAwaitAsync(acceptTask).ConfigureAwait(false);
        }

        foreach (Task task in clientTasks)
        {
            await SafeAwaitAsync(task).ConfigureAwait(false);
        }

        cancellation?.Dispose();
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    public async ValueTask DisposeAsync()
    {
        Task disposeTask;
        lock (_lifecycleGate)
        {
            if (_disposeTask is null)
            {
                Volatile.Write(ref _disposed, 1);
                disposeTask = _disposeTask = StopCoreAsync();
            }
            else
            {
                disposeTask = _disposeTask;
            }
        }

        await disposeTask.ConfigureAwait(false);
    }

    /// <summary>
    /// Pushes one payload to every connected client. Each send runs independently with a
    /// bounded wait; a client that stalls past the timeout is disconnected explicitly.
    /// </summary>
    public async Task BroadcastAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        if (payload.Length == 0 || _clients.IsEmpty)
        {
            return;
        }

        List<Task> sends = [];
        foreach (TcpClient client in _clients.Keys)
        {
            sends.Add(SendToClientAsync(client, payload, cancellationToken));
        }

        await Task.WhenAll(sends).ConfigureAwait(false);
    }

    public Task SendToClientAsync(TcpClient client, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        return payload.Length == 0 || !_clients.ContainsKey(client)
            ? Task.CompletedTask
            : SendToClientCoreAsync(client, payload, cancellationToken);
    }

    public void Disconnect(TcpClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        DisconnectClient(client);
    }

    private static async Task SafeAwaitAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (SocketException)
        {
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task SendToClientCoreAsync(TcpClient client, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        try
        {
            using CancellationTokenSource bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            bounded.CancelAfter(SlowClientSendTimeout);
            NetworkStream stream = client.GetStream();
            await stream.WriteAsync(payload, bounded.Token).ConfigureAwait(false);
            await stream.FlushAsync(bounded.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            BridgeDiagnostic?.Invoke($"Telnet client dropped ({client.Client.RemoteEndPoint}): {exception.Message}");
            DisconnectClient(client);
        }
    }

    private void DisconnectClient(TcpClient client)
    {
        if (_clients.TryRemove(client, out _))
        {
            client.Dispose();
            ClientDisconnected?.Invoke(client);
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            _clients.TryAdd(client, 0);
            StatusChanged?.Invoke(this, EventArgs.Empty);
            Task handler = HandleClientAsync(client, cancellationToken);
            lock (_lifecycleGate)
            {
                _clientTasks.Add(handler);
                _clientTasks.RemoveAll(task => task.IsCompleted);
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        try
        {
            using NetworkStream stream = client.GetStream();
            byte[] welcomeText = Encoding.UTF8.GetBytes("DuCom Telnet bridge ready.\r\n");
            byte[] welcome = new byte[6 + welcomeText.Length];
            byte[] negotiation = [255, 253, 3, 255, 251, 3]; // DO/WILL Suppress Go Ahead
            negotiation.CopyTo(welcome, 0);
            welcomeText.CopyTo(welcome, negotiation.Length);
            await stream.WriteAsync(welcome, cancellationToken).ConfigureAwait(false);
            ClientConnected?.Invoke(client);
            byte[] buffer = new byte[4_096];
            while (!cancellationToken.IsCancellationRequested)
            {
                int length = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (length == 0)
                {
                    break;
                }

                ClientDataReceived?.Invoke(client, buffer.AsMemory(0, length));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
        catch (SocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            DisconnectClient(client);
        }
    }
}
