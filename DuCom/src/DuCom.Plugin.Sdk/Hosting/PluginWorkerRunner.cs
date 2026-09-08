using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;
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

public sealed class PluginWorkerRunner : IPluginRequestChannel, IDisposable
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

    private async Task HandleRequestAsync(PluginWireMessage message)
    {
        JsonElement? data = message.Data;
        try
        {
            switch (message.Operation)
            {
                case PluginOps.WorkerInit:
                    _plugin = LoadPlugin();
                    _plugin.Runner = this;
                    _plugin.BindHostApi(_facades);
                    using (CancellationTokenSource init = new(TimeSpan.FromMilliseconds(Math.Max(_startup.Limits.StartupTimeoutMs - 2000, 2000))))
                    {
                        await _plugin.InitializeAsync(init.Token);
                    }

                    break;
                case PluginOps.PluginActivate:
                    if (_plugin is null)
                    {
                        throw new InvalidOperationException("Plugin is not initialized.");
                    }

                    using (CancellationTokenSource activation = new(TimeSpan.FromMilliseconds(Math.Max(_startup.Limits.ActivationTimeoutMs - 2000, 2000))))
                    {
                        PluginActivation result = await _plugin.ActivateAsync(activation.Token);
                        if (!UiContributionValidator.Validate(result, out string? error))
                        {
                            throw new InvalidOperationException(error);
                        }

                        await RespondAsync(message, JsonSerializer.SerializeToElement(SerializeActivation(result)));
                        return;
                    }
                case PluginOps.PluginDeactivate:
                    if (_plugin is not null)
                    {
                        using CancellationTokenSource grace = new(TimeSpan.FromMilliseconds(Math.Max(_startup.Limits.StopGraceMs - 500, 500)));
                        await _plugin.DeactivateAsync(grace.Token);
                    }

                    await RespondAsync(message, JsonSerializer.SerializeToElement(new { stopped = true }));
                    _shutdown = true;
                    return;
                case PluginOps.CommandInvoke:
                    if (_plugin is null)
                    {
                        throw new InvalidOperationException("Plugin is not initialized.");
                    }

                    CommandInvokeNotice notice = Deserialize<CommandInvokeNotice>(data);
                    using (CancellationTokenSource call = new(TimeSpan.FromMilliseconds(Math.Max(_startup.Limits.CallTimeoutMs - 200, 200))))
                    {
                        CommandInvokeOutcome outcome = await _plugin.OnCommandAsync(notice.CommandId, notice.Arg, notice.Values, call.Token);
                        await RespondAsync(message, JsonSerializer.SerializeToElement(outcome));
                        return;
                    }
                case PluginOps.SettingsApply:
                    if (_plugin is null)
                    {
                        throw new InvalidOperationException("Plugin is not initialized.");
                    }

                    SettingsApplyNotice applyNotice = Deserialize<SettingsApplyNotice>(data);
                    using (CancellationTokenSource call = new(TimeSpan.FromMilliseconds(Math.Max(_startup.Limits.CallTimeoutMs - 200, 200))))
                    {
                        SettingsApplyOutcome outcome = await _plugin.OnSettingsApplyAsync(applyNotice.Values, call.Token);
                        await RespondAsync(message, JsonSerializer.SerializeToElement(outcome));
                        return;
                    }
                case PluginOps.TaskCancel:
                    TaskCancelNotice cancelNotice = Deserialize<TaskCancelNotice>(data);
                    CancelPluginTask(cancelNotice.TaskId);
                    break;
                default:
                    await RespondErrorAsync(message, PluginErrorCode.UnsupportedOperation, $"Unknown operation '{message.Operation}'.");
                    return;
            }

            await RespondAsync(message, JsonSerializer.SerializeToElement(new { ok = true }));
        }
        catch (OperationCanceledException)
        {
            await RespondErrorAsync(message, PluginErrorCode.DeadlineExceeded, $"Operation '{message.Operation}' timed out in the worker.");
        }
        catch (Exception exception)
        {
            await RespondErrorAsync(message, PluginErrorCode.InternalError, exception.Message);
        }
    }

    private async Task HandleNotificationAsync(PluginWireMessage message)
    {
        switch (message.Operation)
        {
            case PluginOps.SerialData:
                SerialDataNotice notice = Deserialize<SerialDataNotice>(message.Data);
                byte[] payload = string.IsNullOrEmpty(notice.B64) ? [] : Convert.FromBase64String(notice.B64);
                while (_serialQueue.Count >= _startup.Limits.SerialQueueBlocks)
                {
                    await _serialQueueSignal.WaitAsync(50, CancellationToken.None);
                    if (_shutdown)
                    {
                        return;
                    }
                }

                _serialQueue.Enqueue(new SerialDataEventArgs(payload, notice.SessionId, notice.Sequence, notice.ByteOffset, notice.ReceivedAtUtc));
                _serialQueueSignal.Release();
                break;
            case PluginOps.SessionClosed:
                SessionClosedNotice closed = Deserialize<SessionClosedNotice>(message.Data);
                _serialSink.RaiseClosed(closed.SessionId);
                _plugin?.OnSessionClosed(closed.SessionId);
                break;
        }
    }

    private async Task SerialPumpAsync()
    {
        while (!_shutdown)
        {
            await _serialQueueSignal.WaitAsync(200, CancellationToken.None);
            while (_serialQueue.TryDequeue(out SerialDataEventArgs? args))
            {
                if (_shutdown)
                {
                    return;
                }

                try
                {
                    _serialSink.RaiseData(args);
                    _plugin?.OnSerialData(args);
                }
                catch (Exception)
                {
                }
            }
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

    private DuComPlugin LoadPlugin()
    {
        string entryAssemblyPath = Path.Combine(_startup.PluginDirectory, _startup.EntryAssembly);
        if (!File.Exists(entryAssemblyPath))
        {
            throw new FileNotFoundException($"Entry assembly '{_startup.EntryAssembly}' not found in the package.");
        }

        PluginLoadContext loadContext = new(entryAssemblyPath);
        Assembly assembly = loadContext.LoadFromAssemblyPath(entryAssemblyPath);
        Type? type = assembly.GetType(_startup.EntryType);
        if (type is null)
        {
            throw new TypeLoadException($"Entry type '{_startup.EntryType}' not found in {assembly.GetName().Name}.");
        }

        if (!typeof(DuComPlugin).IsAssignableFrom(type))
        {
            throw new InvalidOperationException($"Entry type '{type.FullName}' does not derive from {nameof(DuComPlugin)}.");
        }

        if (type.GetConstructor(Type.EmptyTypes) is null)
        {
            throw new InvalidOperationException($"Entry type '{type.FullName}' must declare a public parameterless constructor.");
        }

        object instance = Activator.CreateInstance(type)!;
        return (DuComPlugin)instance;
    }

    private async Task NotifyProgressSafeAsync(TaskProgressNotice notice)
    {
        try
        {
            await NotifyAsync(PluginOps.TaskProgress, notice, CancellationToken.None);
        }
        catch (Exception)
        {
        }
    }

    internal void StartTask(DuComPlugin plugin, string taskId, Func<PluginTaskContext, Task> taskBody)
    {
        lock (_taskGate)
        {
            if (_tasks.ContainsKey(taskId))
            {
                throw new ArgumentException($"Task '{taskId}' already exists.");
            }

            CancellationTokenSource cts = new();
            PluginTaskContext context = new(taskId, cts.Token)
            {
                ProgressReporter = notice =>
                {
                    _ = NotifyProgressSafeAsync(notice);
                },
            };
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(_startup.Limits.TaskDeadlineMs, 1000));
            _tasks[taskId] = (context, cts, deadline);
            _ = RunTaskAsync(plugin, taskId, taskBody, context, cts, deadline);
        }
    }

    private async Task RunTaskAsync(DuComPlugin plugin, string taskId, Func<PluginTaskContext, Task> taskBody, PluginTaskContext context, CancellationTokenSource cts, DateTime deadlineUtc)
    {
        try
        {
            Task body = taskBody(context);
            Task expiry = Task.Delay(deadlineUtc - DateTime.UtcNow, cts.Token);
            Task finished = await Task.WhenAny(body, expiry);
            if (ReferenceEquals(finished, expiry))
            {
                cts.Cancel();
                try
                {
                    await body.WaitAsync(TimeSpan.FromSeconds(2));
                }
                catch (Exception)
                {
                }
            }
            else
            {
                await body;
            }
        }
        catch (Exception)
        {
        }
        finally
        {
            context.Complete();
            cts.Dispose();
            lock (_taskGate)
            {
                _tasks.Remove(taskId);
            }
        }
    }

    internal bool CancelPluginTask(string taskId)
    {
        lock (_taskGate)
        {
            if (_tasks.TryGetValue(taskId, out (PluginTaskContext Context, CancellationTokenSource Cts, DateTime DeadlineUtc) entry))
            {
                entry.Context.Cancel();
                entry.Cts.Cancel();
                return true;
            }
        }

        return false;
    }

    private async Task CancelAllTasksAsync()
    {
        List<(PluginTaskContext Context, CancellationTokenSource Cts, DateTime DeadlineUtc)> entries;
        lock (_taskGate)
        {
            entries = [.. _tasks.Values];
        }

        foreach ((PluginTaskContext context, CancellationTokenSource cts, _) in entries)
        {
            context.Cancel();
            cts.Cancel();
        }
    }

    private static Dictionary<string, object> SerializeActivation(PluginActivation activation) => new()
    {
        ["menus"] = activation.Menus,
        ["settingsPanels"] = activation.SettingsPanels,
        ["toolPages"] = activation.ToolPages,
        ["backgrounds"] = activation.BackgroundImages,
    };

    private static T Deserialize<T>(JsonElement? element) =>
        element is null
            ? throw new PluginHostException(PluginErrorCode.InvalidArgument, "Request payload is missing.")
            : element.Value.Deserialize<T>(DtoJson.Options)
                ?? throw new PluginHostException(PluginErrorCode.InvalidArgument, "Request payload is malformed.");

    public async Task<JsonElement?> RequestAsync(string operation, object? request, CancellationToken cancellationToken)
    {
        string requestId = $"w{Interlocked.Increment(ref _requestCounter)}";
        PluginWireMessage message = PluginWireMessage.Request(
            _startup.SessionId,
            _startup.ActivationId,
            requestId,
            operation,
            SerializePayload(request));
        TaskCompletionSource<PluginWireMessage> waiter = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingRequests[requestId] = waiter;
        try
        {
            await WriteMessageAsync(message, cancellationToken);
            PluginWireMessage response = await waiter.Task.WaitAsync(cancellationToken);
            if (response.Error is { } error)
            {
                throw new PluginHostException(error.Code, error.Message);
            }

            return response.Data;
        }
        finally
        {
            _pendingRequests.TryRemove(requestId, out _);
        }
    }

    public async Task NotifyAsync(string operation, object? payload, CancellationToken cancellationToken) =>
        await WriteMessageAsync(
            PluginWireMessage.Notify(_startup.SessionId, _startup.ActivationId, operation, SerializePayload(payload)),
            cancellationToken);

    private static JsonElement? SerializePayload(object? payload) =>
        payload is null
            ? null
            : JsonSerializer.SerializeToElement(payload, DtoJson.Options);

    private async Task RespondAsync(PluginWireMessage request, JsonElement data) =>
        await WriteMessageAsync(PluginWireMessage.Response(_startup.SessionId, _startup.ActivationId, request.RequestId ?? string.Empty, data));

    private async Task RespondErrorAsync(PluginWireMessage request, string code, string message) =>
        await WriteMessageAsync(PluginWireMessage.ErrorResponse(_startup.SessionId, _startup.ActivationId, request.RequestId ?? string.Empty, code, message));

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


    private sealed class PluginLoadContext(string entryAssemblyPath) : AssemblyLoadContext(isCollectible: false)
    {
        private readonly AssemblyDependencyResolver _resolver = new(entryAssemblyPath);
        private const string SdkAssemblyName = "DuCom.Plugin.Sdk";

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (string.Equals(assemblyName.Name, SdkAssemblyName, StringComparison.Ordinal))
            {
                return null;
            }

            string? path = _resolver.ResolveAssemblyToPath(assemblyName);
            return path is not null && File.Exists(path) ? LoadFromAssemblyPath(path) : null;
        }
    }
}



