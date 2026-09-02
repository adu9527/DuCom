using DuCom.Core.Telnet;

namespace DuCom.Services;

/// <summary>
/// Application shell over <see cref="TelnetBridgeCore"/>: binds one serial session to the
/// Telnet server using immutable UI-thread session probes and routes diagnostics to the
/// application log. Push framing, client-command framing, task tracking, and disposal
/// semantics live in the Core type and are covered by Core tests.
/// </summary>
public sealed class TelnetBridgeService : IAsyncDisposable
{
    private readonly BasicTelnetServer _server;
    private readonly TelnetBridgeCore _core;
    private int _disposed;

    public TelnetBridgeService(SessionProbeProvider probes, BasicTelnetServer server)
    {
        ArgumentNullException.ThrowIfNull(probes);
        _server = server ?? throw new ArgumentNullException(nameof(server));
        _core = new TelnetBridgeCore(
            server,
            probes.FindTelnetProbe,
            message => Program.DiagnosticLog?.Information(message));
        _server.StatusChanged += OnServerStatusChanged;
        _server.BridgeDiagnostic += OnBridgeDiagnostic;
    }

    public event EventHandler? StatusChanged;

    public event Action<string>? Diagnostic;

    public bool IsRunning => _server.IsRunning;

    public int ClientCount => _server.ClientCount;

    public string? LocalEndPoint => _server.LocalEndPoint;

    public IReadOnlyList<string> ClientEndpoints => _server.ClientEndpoints;

    public string? BoundPortName => _core.BoundPortName;

    public bool IsBound => _core.IsBound;

    public void ConfigureAuthentication(TelnetAuthenticationOptions options) => _core.ConfigureAuthentication(options);

    public void Start(TelnetListenOptions options) => _server.Start(options);

    public Task StopAsync() => _server.StopAsync();

    /// <summary>Binds to a port name and starts pushing lines. Rebinding resets the cursor.</summary>
    public void Bind(string portName) => _core.Bind(portName);

    public void Unbind() => _core.Unbind();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _server.StatusChanged -= OnServerStatusChanged;
        _server.BridgeDiagnostic -= OnBridgeDiagnostic;
        await _core.DisposeAsync().ConfigureAwait(false);
        await _server.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private void OnServerStatusChanged(object? sender, EventArgs e) => StatusChanged?.Invoke(this, e);

    private void OnBridgeDiagnostic(string message) => Diagnostic?.Invoke(message);
}
