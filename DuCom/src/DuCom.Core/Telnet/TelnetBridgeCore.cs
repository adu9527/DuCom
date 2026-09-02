using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using DuCom.Core.Diagnostics;
using DuCom.Core.Parsing;
using DuCom.Core.Storage;

namespace DuCom.Core.Telnet;

/// <summary>
/// Immutable, delegate-only view of one serial session consumed by the Telnet bridge. The
/// application layer builds these on the UI thread; the bridge never touches a ViewModel
/// property from its background threads. <see cref="SendAsync"/> routes through the serial
/// session send path (TX log included); the session itself rejects sends after close.
/// </summary>
public sealed record TelnetSessionProbe(
    string PortName,
    Func<LineCursor?, LineStoreSnapshot> PullLines,
    Func<string, CancellationToken, Task> SendAsync);

/// <summary>
/// Core of the Telnet serial bridge: binds one serial session by port name, pushes new
/// display lines (RX, ANSI-stripped) to every Telnet client once per second, and frames
/// client input into line commands sent through the session. All tasks — the push loop and
/// every client-command send — are part of the instance lifecycle; disposal cancels and
/// waits for them. Sessions that disappear stop the push until rebound, never drop data
/// silently: clients resume from the session's current end.
/// </summary>
public sealed class TelnetBridgeCore : IAsyncDisposable
{
    private readonly BasicTelnetServer _server;
    private readonly Func<string?, TelnetSessionProbe?> _findSession;
    private readonly Action<string>? _diagnosticLog;
    private readonly PeriodicBackgroundWorker _worker;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly ConcurrentDictionary<TcpClient, IncrementalUtf8LineFramer> _framers = new();
    private readonly ConcurrentDictionary<TcpClient, TelnetNegotiationFilter> _negotiationFilters = new();
    private readonly ConcurrentDictionary<TcpClient, ClientShellState> _clientStates = new();
    private readonly object _gate = new();
    private readonly List<Task> _commandTasks = [];
    private string? _boundPortName;
    private LineCursor? _cursor;
    private Task? _disposeTask;
    private TelnetAuthenticationOptions _authentication = TelnetAuthenticationOptions.Disabled;

    public TelnetBridgeCore(
        BasicTelnetServer server,
        Func<string?, TelnetSessionProbe?> findSession,
        Action<string>? diagnosticLog = null)
    {
        _server = server ?? throw new ArgumentNullException(nameof(server));
        _findSession = findSession ?? throw new ArgumentNullException(nameof(findSession));
        _diagnosticLog = diagnosticLog;
        _worker = new PeriodicBackgroundWorker(
            "telnet-bridge-push",
            TimeSpan.FromSeconds(1),
            PushNewLinesAsync,
            (name, exception) => _diagnosticLog?.Invoke($"{name} tick failed: {exception.Message}"));
        _server.ClientDataReceived += OnClientDataReceived;
        _server.ClientConnected += OnClientConnected;
        _server.ClientDisconnected += OnClientDisconnected;
    }

    public string? BoundPortName
    {
        get
        {
            lock (_gate)
            {
                return _boundPortName;
            }
        }
    }

    public bool IsBound => BoundPortName is not null;

    public void ConfigureAuthentication(TelnetAuthenticationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
            _authentication = options;
        }
    }

    /// <summary>Binds to a port name and starts pushing lines. Rebinding resets the cursor.</summary>
    public void Bind(string portName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(portName);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
            _boundPortName = portName;
            TelnetSessionProbe? probe = _findSession(portName);
            _cursor = probe is null ? null : GetEndCursor(probe.PullLines(null));
            _worker.Start(TimeSpan.FromSeconds(1));
        }

        _diagnosticLog?.Invoke($"Telnet bridge bound. Port={portName}");
    }

    public void Unbind()
    {
        lock (_gate)
        {
            _boundPortName = null;
            _cursor = null;
        }

        _diagnosticLog?.Invoke("Telnet bridge unbound.");
    }

    internal static LineCursor? GetEndCursor(LineStoreSnapshot snapshot) =>
        snapshot.LastLogicalId is long lastId
            ? new LineCursor(lastId, snapshot.Lines.Count > 0 ? snapshot.Lines[^1].SegmentIndex : 0)
            : null;

    private async Task PushNewLinesAsync(CancellationToken cancellationToken)
    {
        string? portName;
        lock (_gate)
        {
            if (_boundPortName is null)
            {
                return;
            }

            portName = _boundPortName;
        }

        TelnetSessionProbe? probe = _findSession(portName);
        if (probe is null)
        {
            // Bound session is gone: stop pushing until it returns, without dropping data
            // (clients resume from the current end when it reconnects).
            lock (_gate)
            {
                _cursor = null;
            }

            return;
        }

        LineCursor? cursor;
        lock (_gate)
        {
            cursor = _cursor;
        }

        LineStoreSnapshot snapshot = probe.PullLines(cursor);
        if (snapshot.Lines.Count == 0)
        {
            return;
        }

        List<string> outbound = [];
        foreach (StoredLine line in snapshot.Lines)
        {
            if (line.Direction == LineDirection.Rx)
            {
                string clean = StripEscapes(line.Text);
                if (clean.Length > 0)
                {
                    outbound.Add(clean);
                }
            }
        }

        lock (_gate)
        {
            if (!string.Equals(_boundPortName, portName, StringComparison.OrdinalIgnoreCase))
            {
                return; // rebound meanwhile; this batch belongs to the old binding
            }

            StoredLine last = snapshot.Lines[^1];
            _cursor = new LineCursor(last.LogicalId, last.SegmentIndex);
        }

        if (outbound.Count > 0)
        {
            string payload = string.Join("\r\n", outbound) + "\r\n";
            byte[] bytes = Encoding.UTF8.GetBytes(payload);
            IEnumerable<TcpClient> clients = _clientStates
                .Where(pair => pair.Value.Stage == ClientShellStage.Ready)
                .Select(pair => pair.Key);
            await Task.WhenAll(clients.Select(client => _server.SendToClientAsync(client, bytes, cancellationToken))).ConfigureAwait(false);
        }
    }

    private static string StripEscapes(string text) =>
        text.Contains('\u001B')
            ? new AnsiDisplayProjector().Project(text, null).DisplayText
            : text;

    private void OnClientDataReceived(TcpClient client, ReadOnlyMemory<byte> data)
    {
        if (data.Length == 0)
        {
            return;
        }

        byte[] filtered = _negotiationFilters.GetOrAdd(client, _ => new TelnetNegotiationFilter()).Filter(data.Span);
        if (filtered.Length == 0)
        {
            return;
        }

        // Per-client incremental framer: multibyte characters and CRLF pairs split across
        // TCP segments stay intact; each completed non-empty line becomes one send.
        IncrementalUtf8LineFramer framer = _framers.GetOrAdd(client, _ => new IncrementalUtf8LineFramer());
        List<string>? lines = null;
        foreach (string line in framer.Append(filtered))
        {
            lines ??= [];
            lines.Add(line);
        }

        if (lines is null)
        {
            return;
        }

        foreach (string line in lines)
        {
            TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                if (_disposeTask is not null)
                {
                    return;
                }

                _commandTasks.Add(completion.Task);
            }

            _ = HandleClientLineSerializedAsync(client, line, completion);
        }
    }

    private async Task HandleClientLineSerializedAsync(TcpClient client, string line, TaskCompletionSource completion)
    {
        try
        {
            if (!_clientStates.TryGetValue(client, out ClientShellState? state))
            {
                return;
            }

            await state.Gate.WaitAsync(_cancellation.Token).ConfigureAwait(false);
            try
            {
                await HandleClientLineAsync(client, state, line).ConfigureAwait(false);
            }
            finally
            {
                state.Gate.Release();
            }
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            completion.TrySetResult();
            lock (_gate)
            {
                _commandTasks.RemoveAll(finished => finished.IsCompleted);
            }
        }
    }

    private async Task HandleClientLineAsync(TcpClient client, ClientShellState state, string line)
    {
        TelnetAuthenticationOptions authentication;
        string? portName;
        lock (_gate)
        {
            authentication = _authentication;
            portName = _boundPortName;
        }

        if (state.Stage == ClientShellStage.Username)
        {
            if (!FixedTimeEquals(line, authentication.Username))
            {
                await SendTextAsync(client, "\r\nAuthentication failed.\r\n").ConfigureAwait(false);
                _server.Disconnect(client);
                return;
            }

            state.Stage = ClientShellStage.Password;
            await SendTextAsync(client, "\r\nPassword: ").ConfigureAwait(false);
            return;
        }

        if (state.Stage == ClientShellStage.Password)
        {
            if (!FixedTimeEquals(line, authentication.Password))
            {
                await SendTextAsync(client, "\r\nAuthentication failed.\r\n").ConfigureAwait(false);
                _server.Disconnect(client);
                return;
            }

            state.Stage = ClientShellStage.Ready;
            await SendTextAsync(client, "\r\nLogin successful.\r\n > ").ConfigureAwait(false);
            return;
        }

        string trimmed = line.Trim();
        if (trimmed.Equals("help", StringComparison.OrdinalIgnoreCase))
        {
            await SendTextAsync(client, "\r\nhelp               show this help\r\nclear              clear the client screen\r\nexit/quit          disconnect\r\nsendtoall <text>   send text to all authenticated clients\r\n > ").ConfigureAwait(false);
            return;
        }

        if (trimmed.Equals("clear", StringComparison.OrdinalIgnoreCase))
        {
            await SendTextAsync(client, "\u001B[1J\u001B[H > ").ConfigureAwait(false);
            return;
        }

        if (trimmed.Equals("exit", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("quit", StringComparison.OrdinalIgnoreCase))
        {
            await SendTextAsync(client, "\r\nGoodbye.\r\n").ConfigureAwait(false);
            _server.Disconnect(client);
            return;
        }

        const string sendToAll = "sendtoall";
        if (trimmed.StartsWith(sendToAll + " ", StringComparison.OrdinalIgnoreCase))
        {
            string message = trimmed[(sendToAll.Length + 1)..];
            byte[] payload = Encoding.UTF8.GetBytes($"\r\n{message}\r\n > ");
            IEnumerable<TcpClient> clients = _clientStates
                .Where(pair => pair.Value.Stage == ClientShellStage.Ready)
                .Select(pair => pair.Key);
            await Task.WhenAll(clients.Select(target => _server.SendToClientAsync(target, payload, _cancellation.Token))).ConfigureAwait(false);
            return;
        }

        if (portName is null)
        {
            await SendTextAsync(client, "\r\nNo serial bridge is bound.\r\n > ").ConfigureAwait(false);
            return;
        }

        TelnetSessionProbe? probe = _findSession(portName);
        if (probe is null)
        {
            _diagnosticLog?.Invoke($"Telnet bridge received data but the bound session is closed. Port={portName}");
            await SendTextAsync(client, "\r\nThe bound serial session is closed.\r\n > ").ConfigureAwait(false);
            return;
        }

        // Register the command with the lifecycle BEFORE running it: disposal snapshots
        // the tracked tasks under the gate, so a task started after registration can
        // never escape the dispose wait (2026-08-28 review).
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_disposeTask is not null)
            {
                return; // disposing: no new commands
            }

            _commandTasks.Add(completion.Task);
        }

        _ = RunClientCommandAsync(probe, portName, line, completion);
    }

    private void OnClientConnected(TcpClient client)
    {
        TelnetAuthenticationOptions authentication;
        lock (_gate)
        {
            authentication = _authentication;
        }

        ClientShellState state = new(authentication.Enabled ? ClientShellStage.Username : ClientShellStage.Ready);
        _clientStates[client] = state;
        _negotiationFilters[client] = new TelnetNegotiationFilter();
        string welcome = authentication.Enabled
            ? "Login: "
            : " > ";
        _ = SendTextAsync(client, welcome);
    }

    private Task SendTextAsync(TcpClient client, string text) =>
        _server.SendToClientAsync(client, Encoding.UTF8.GetBytes(text), _cancellation.Token);

    private static bool FixedTimeEquals(string supplied, string expected)
    {
        byte[] suppliedBytes = Encoding.UTF8.GetBytes(supplied);
        byte[] expectedBytes = Encoding.UTF8.GetBytes(expected);
        return suppliedBytes.Length == expectedBytes.Length &&
            CryptographicOperations.FixedTimeEquals(suppliedBytes, expectedBytes);
    }

    private async Task RunClientCommandAsync(TelnetSessionProbe probe, string portName, string line, TaskCompletionSource completion)
    {
        try
        {
            await probe.SendAsync(line, _cancellation.Token).ConfigureAwait(false);
            _diagnosticLog?.Invoke($"Telnet bridge client->serial. Port={portName}; Command length={line.Length}");
            completion.TrySetResult();
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
            completion.TrySetResult();
        }
        catch (Exception exception)
        {
            // Includes a session that closed between framing and sending: the serial send
            // path rejects it; the bridge logs instead of dropping silently or crashing.
            _diagnosticLog?.Invoke($"Telnet bridge send failed. Port={portName}; {exception.Message}");
            completion.TrySetResult();
        }
        finally
        {
            lock (_gate)
            {
                _commandTasks.RemoveAll(finished => finished.IsCompleted);
            }
        }
    }

    private void OnClientDisconnected(TcpClient client)
    {
        _framers.TryRemove(client, out _);
        _negotiationFilters.TryRemove(client, out _);
        _clientStates.TryRemove(client, out _);
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _disposeTask ??= DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        _server.ClientDataReceived -= OnClientDataReceived;
        _server.ClientConnected -= OnClientConnected;
        _server.ClientDisconnected -= OnClientDisconnected;
        _cancellation.Cancel();
        try
        {
            await _worker.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
        }

        Task[] pending;
        lock (_gate)
        {
            pending = [.. _commandTasks];
            _commandTasks.Clear();
        }

        foreach (Task task in pending)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch
            {
                // Command failures are logged where they happen; disposal only waits.
            }
        }

        _cancellation.Dispose();
    }

    private sealed class ClientShellState(ClientShellStage stage)
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public ClientShellStage Stage { get; set; } = stage;
    }

    private enum ClientShellStage
    {
        Username,
        Password,
        Ready,
    }
}
