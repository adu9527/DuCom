using System.Collections.Concurrent;
using System.Text.Json;
using DuCom.Plugin.Dto;

namespace DuCom.Plugin;

public sealed record PluginWorkerStartup
{
    public string PipeName { get; init; } = string.Empty;
    public string Credential { get; init; } = string.Empty;
    public string SessionId { get; init; } = string.Empty;
    public string ActivationId { get; init; } = string.Empty;
    public string PluginDirectory { get; init; } = string.Empty;
    public string EntryAssembly { get; init; } = string.Empty;
    public string EntryType { get; init; } = string.Empty;
    public string PluginId { get; init; } = string.Empty;
    public string PluginVersion { get; init; } = string.Empty;
    public string PackageDigest { get; init; } = string.Empty;
    public string HostVersion { get; init; } = string.Empty;
    public string Culture { get; init; } = "en-US";
    public IReadOnlyList<string> Capabilities { get; init; } = [];
    public IReadOnlyList<string> Permissions { get; init; } = [];
    public PluginLimits Limits { get; init; } = new();

    public static PluginWorkerStartup ParseJson(string json)
    {
        PluginWorkerStartup? startup = JsonSerializer.Deserialize<PluginWorkerStartup>(json, DtoJson.Options);
        return startup ?? throw new InvalidOperationException("Worker startup configuration is empty.");
    }
}

public sealed partial class PluginWorkerRunner : IPluginRequestChannel, IDisposable
{
    private readonly Stream _stream;
    private readonly PluginWorkerStartup _startup;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<PluginWireMessage>> _pendingRequests = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<SerialDataEventArgs> _serialQueue = new();
    private readonly SemaphoreSlim _serialQueueSignal = new(0);
    private readonly object _taskGate = new();
    private readonly Dictionary<string, (PluginTaskContext Context, CancellationTokenSource Cts, DateTime DeadlineUtc)> _tasks = new(StringComparer.Ordinal);
    private readonly PluginApiFacades _facades;
    private readonly ISerialEventSink _serialSink;
    private DuComPlugin? _plugin;
    private int _requestCounter;
    private volatile bool _shutdown;

    private PluginWorkerRunner(Stream stream, PluginWorkerStartup startup)
    {
        _stream = stream;
        _startup = startup;
        _facades = new PluginApiFacades(this)
        {
            PluginId = startup.PluginId,
            PluginVersion = startup.PluginVersion,
            Culture = startup.Culture,
            Limits = startup.Limits,
            GrantedCapabilities = startup.Capabilities,
            GrantedPermissions = startup.Permissions,
        };
        _serialSink = _facades.SerialSink;
    }

    public static async Task<int> RunAsync(Stream pipeStream, PluginWorkerStartup startup)
    {
        ArgumentNullException.ThrowIfNull(pipeStream);
        ArgumentNullException.ThrowIfNull(startup);
        PluginWorkerRunner runner = new(pipeStream, startup);
        return await runner.RunInternalAsync();
    }

    private async Task<int> RunInternalAsync()
    {
        PluginWireMessage hello = PluginWireMessage.Notify(_startup.SessionId, _startup.ActivationId, PluginOps.WorkerHello, JsonSerializer.SerializeToElement(new Dictionary<string, string>
        {
            ["credential"] = _startup.Credential,
            ["runnerVersion"] = typeof(PluginWorkerRunner).Assembly.GetName().Version?.ToString() ?? "0.0.0",
            ["pluginId"] = _startup.PluginId,
            ["pluginVersion"] = _startup.PluginVersion,
            ["packageDigest"] = _startup.PackageDigest,
            ["protocolVersion"] = PluginWire.ProtocolVersion,
        }));

        await WriteMessageAsync(hello, CancellationToken.None);

        Thread heartbeat = new(HeartbeatLoop) { IsBackground = true, Name = "ducom-plugin-heartbeat" };
        heartbeat.Start();
        Task pump = Task.Run(SerialPumpAsync);

        try
        {
            while (!_shutdown)
            {
                PluginWireMessage? message = await PluginWire.ReadAsync(_stream, CancellationToken.None);
                if (message is null)
                {
                    break;
                }

                if (!string.Equals(message.SessionId, _startup.SessionId, StringComparison.Ordinal)
                    || !string.Equals(message.ActivationId, _startup.ActivationId, StringComparison.Ordinal))
                {
                    continue;
                }

                switch (message.Kind)
                {
                    case WireKinds.Request:
                        _ = Task.Run(() => HandleRequestAsync(message));
                        break;
                    case WireKinds.Response:
                        if (message.RequestId is { } requestId
                            && _pendingRequests.TryRemove(requestId, out TaskCompletionSource<PluginWireMessage>? waiter))
                        {
                            waiter.TrySetResult(message);
                        }

                        break;
                    case WireKinds.Notification:
                        await HandleNotificationAsync(message);
                        break;
                }
            }

            return 0;
        }
        catch (Exception)
        {
            return -1;
        }
        finally
        {
            _shutdown = true;
            _serialQueueSignal.Release();
            await CancelAllTasksAsync();
            try
            {
                if (_plugin is not null)
                {
                    using CancellationTokenSource grace = new(TimeSpan.FromMilliseconds(Math.Min(_startup.Limits.StopGraceMs, 1500)));
                    await _plugin.DeactivateAsync(grace.Token);
                }
            }
            catch (Exception)
            {
            }

            foreach (TaskCompletionSource<PluginWireMessage> waiter in _pendingRequests.Values)
            {
                waiter.TrySetException(new IOException("Plugin channel closed."));
            }

            _pendingRequests.Clear();
        }
    }

    private void HeartbeatLoop()
    {
        int interval = Math.Max(_startup.Limits.HeartbeatIntervalMs, 250);
        while (!_shutdown)
        {
            try
            {
                Thread.Sleep(interval);
                if (_shutdown)
                {
                    return;
                }

                PluginWireMessage heartbeat = PluginWireMessage.Notify(
                    _startup.SessionId,
                    _startup.ActivationId,
                    PluginOps.WorkerHeartbeat,
                    JsonSerializer.SerializeToElement(new HeartbeatNotice
                    {
                        TimestampUtc = DateTimeOffset.UtcNow,
                        PendingEvents = _serialQueue.Count,
                        ManagedMemory = GC.GetTotalMemory(false),
                    }));
                _writeLock.Wait();
                try
                {
                    PluginWire.WriteAsync(_stream, heartbeat, CancellationToken.None).Wait(interval);
                }
                finally
                {
                    _writeLock.Release();
                }
            }
            catch (Exception)
            {
                return;
            }
        }
    }

    private async Task WriteMessageAsync(PluginWireMessage message, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await PluginWire.WriteAsync(_stream, message, cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public void Dispose()
    {
        _writeLock.Dispose();
        _serialQueueSignal.Dispose();
    }


}



