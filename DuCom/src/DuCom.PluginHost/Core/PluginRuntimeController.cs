using System.Text.Json;
using DuCom.Plugin;
using DuCom.Plugin.Dto;
using DuCom.PluginHost.Core;
using DuCom.PluginHost.Diagnostics;
using DuCom.PluginHost.Registry;
using DuCom.PluginHost.Security;
using DuCom.PluginHost.Transport;

namespace DuCom.PluginHost.Core;

public enum PluginRuntimeState
{
    Discovered,
    Validated,
    Starting,
    Activating,
    Active,
    Stopping,
    Disabled,
    FaultDisabled,
    Rejected,
    StoppedByBudget,
}

public sealed record PluginStateChange(string PluginId, PluginRuntimeState Previous, PluginRuntimeState Next, string? Reason);

public sealed class SerialSubscriptionState
{
    public required string SubscriptionId { get; init; }
    public required string SessionId { get; init; }
    public long NextSequence { get; set; }
    public long DroppedBlocks { get; set; }
    public long? GapFrom { get; set; }
    public Queue<(long Timestamp, bool Delivered)> Window { get; } = new();
}

public sealed class PluginRuntimeController : IAsyncDisposable
{
    private readonly PluginManifest _manifest;
    private readonly string _versionDirectory;
    private readonly string _digest;
    private readonly PluginHostPaths _paths;
    private readonly SandboxLauncher _launcher;
    private readonly IPluginHostEnvironment _environment;
    private readonly PluginLimits _limits;
    private readonly PluginDiagnosticsLog _diagnostics;
    private readonly BudgetGovernor _budget;
    private readonly PluginRegistryStore _registry;
    private readonly object _gate = new();
    private readonly Dictionary<string, TaskCompletionSource<PluginWireMessage>> _pendingHostRequests = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SerialSubscriptionState> _serialSubscriptions = new(StringComparer.Ordinal);
    private readonly HashSet<string> _workerRequestIds = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _workerRequestSlots = new(initialCount: 8, maxCount: 8);
    private readonly IReadOnlyList<string> _grantedPermissions;
    private readonly HostTempDiskBudget _hostTempDiskBudget;
    private readonly HostTempResourceLedger _hostTempLedger;

    private PluginRuntimeState _state = PluginRuntimeState.Discovered;
    private PluginPipeServer? _pipe;
    private WorkerSpawnResult? _worker;
    private ActivationScope? _scope;
    private PluginBroker? _broker;
    private IDisposable? _rawTap;
    private Timer? _watchdog;
    private DateTimeOffset _lastHeartbeatUtc;
    private string _activationId = string.Empty;
    private string _sessionId = string.Empty;
    private int _hostRequestCounter;
    private bool _faultNotified;
    private int _faultHandling;
    private PluginPublishedActivation? _published;
    private CancellationTokenSource? _activationCancellation;

    public PluginRuntimeController(
        PluginManifest manifest,
        string versionDirectory,
        string digest,
        PluginHostPaths paths,
        SandboxLauncher launcher,
        IPluginHostEnvironment environment,
        PluginLimits limits,
        PluginDiagnosticsLog diagnostics,
        BudgetGovernor budget,
        PluginRegistryStore registry,
        IReadOnlyList<string> grantedPermissions,
        HostTempDiskBudget hostTempDiskBudget,
        HostTempResourceLedger hostTempLedger)
    {
        _manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        _versionDirectory = versionDirectory;
        _digest = digest;
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _limits = limits;
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _budget = budget ?? throw new ArgumentNullException(nameof(budget));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _grantedPermissions = grantedPermissions ?? throw new ArgumentNullException(nameof(grantedPermissions));
        _hostTempDiskBudget = hostTempDiskBudget ?? throw new ArgumentNullException(nameof(hostTempDiskBudget));
        _hostTempLedger = hostTempLedger ?? throw new ArgumentNullException(nameof(hostTempLedger));
    }

    public event Action<PluginRuntimeController, PluginStateChange>? StateChanged;

    public event Action<HostFaultNotice>? FaultNotice;

    public PluginManifest Manifest => _manifest;

    public string PackageDigest => _digest;

    public IReadOnlyList<string> GrantedPermissions => _grantedPermissions;

    public PluginRuntimeState State => _state;

    public string ActivationId => _activationId;

    public PluginPublishedActivation? Published => _published;

    public uint WorkerPid => _worker?.ProcessId ?? 0;

    public long WorkerPrivateBytes => _budget.GetWorkerBytes(WorkerPid);

    public IReadOnlyList<(DateTimeOffset Timestamp, PluginLogLevel Level, string Message)> DiagnosticsSnapshot => _diagnostics.Snapshot();

    public bool IsActive => _state is PluginRuntimeState.Active or PluginRuntimeState.Activating;

    private void Transition(PluginRuntimeState next, string? reason)
    {
        PluginRuntimeState previous;
        lock (_gate)
        {
            if (_state == next)
            {
                return;
            }

            previous = _state;
            _state = next;
        }

        StateChanged?.Invoke(this, new PluginStateChange(_manifest.Id, previous, next, reason));
    }

    public async Task<bool> StartAsync()
    {
        lock (_gate)
        {
            if (_state is PluginRuntimeState.Active or PluginRuntimeState.Starting or PluginRuntimeState.Activating)
            {
                return false;
            }
        }

        Transition(PluginRuntimeState.Starting, null);
        _faultNotified = false;
        _activationId = Guid.NewGuid().ToString("N");
        _sessionId = Guid.NewGuid().ToString("N");
        _activationCancellation = new CancellationTokenSource();

        _registry.Mutate(data =>
        {
            PluginRegistryEntry entry = EnsureEntry(data);
            data.Plugins[_manifest.Id] = entry with
            {
                LastAttempt = new AttemptRecord
                {
                    ActivationId = _activationId,
                    Version = _manifest.Version,
                    Digest = _digest,
                    HostRunId = _registry.HostRunId,
                    StartedUtc = DateTime.UtcNow,
                    EndedCleanly = null,
                },
            };

            return data;
        });

        try
        {
            string storageDirectory = Path.Combine(_paths.StorageRoot, _manifest.Id);
            string workerScratchDirectory = Path.Combine(_paths.TempRoot, "WorkerScratch", _manifest.Id, _activationId);
            string hostOutputDirectory = Path.Combine(_paths.TempRoot, "HostOutput", _manifest.Id, _activationId);
            string hostSnapshotDirectory = Path.Combine(_paths.TempRoot, "HostSnapshots", _manifest.Id, _activationId);
            Directory.CreateDirectory(storageDirectory);
            Directory.CreateDirectory(workerScratchDirectory);
            Directory.CreateDirectory(hostOutputDirectory);
            Directory.CreateDirectory(hostSnapshotDirectory);

            _scope = new ActivationScope(_manifest, _activationId, storageDirectory, workerScratchDirectory, hostOutputDirectory, hostSnapshotDirectory, _limits, _grantedPermissions, _hostTempDiskBudget, _hostTempLedger);
            _broker = new PluginBroker(_scope, _environment, _diagnostics);
            _broker.SerialSubscriptionAdded += (subscriptionId, sessionId) => RegisterSerialSubscription(subscriptionId, sessionId);
            _broker.SerialSubscriptionRemoved += (_, subscriptionId) => RemoveSerialSubscription(subscriptionId);

            string pipeName = $"DuCom.Plugin.{Guid.NewGuid():N}";
            string credential = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
            string appContainerSid = SandboxLauncher.EnsureAppContainerProfile(_manifest.Id).Value;
            _pipe = PluginPipeServer.Create(pipeName, appContainerSid);
            _pipe.MessageReceived += OnWorkerMessage;
            _pipe.ConnectionClosed += OnConnectionClosed;

            _worker = _launcher.Spawn(
                new WorkerStartupConfig
                {
                    PluginId = _manifest.Id,
                    PluginVersion = _manifest.Version,
                    PackageDigest = _digest,
                    PluginDirectory = _versionDirectory,
                    EntryAssembly = _manifest.EntryAssembly,
                    EntryType = _manifest.EntryType,
                    HostVersion = _environment.HostVersion,
                    Culture = _environment.Culture,
                    Capabilities = _manifest.Capabilities,
                    Permissions = _grantedPermissions,
                    Limits = _limits,
                },
                storageDirectory,
                workerScratchDirectory,
                hostOutputDirectory,
                hostSnapshotDirectory,
                _budget.Configuration.PerPluginQuotaBytes,
                pipeName,
                credential,
                _sessionId,
                _activationId);

            _budget.RegisterWorker(_manifest.Id, _worker.ProcessId);

            bool connected = await _pipe.WaitForConnectionAndAuthenticateAsync(
                credential,
                _worker.ProcessId,
                _sessionId,
                _activationId,
                TimeSpan.FromMilliseconds(_limits.StartupTimeoutMs)).ConfigureAwait(false);
            if (!connected)
            {
                throw new TimeoutException("Worker did not connect and authenticate within the startup deadline.");
            }

            _lastHeartbeatUtc = DateTimeOffset.UtcNow;
            _rawTap = _environment.SubscribeRawBlocks(OnRawBlock);
            _environment.SessionClosed += OnEnvironmentSessionClosed;

            PluginWireMessage? initResponse = await SendRequestAsync(
                PluginOps.WorkerInit,
                JsonSerializer.SerializeToElement(new PluginHandshakeInfo
                {
                    PluginId = _manifest.Id,
                    PluginVersion = _manifest.Version,
                    PackageDigest = _digest,
                    SessionId = _sessionId,
                    ActivationId = _activationId,
                    HostVersion = _environment.HostVersion,
                    Culture = _environment.Culture,
                    GrantedCapabilities = _manifest.Capabilities,
                    GrantedPermissions = _grantedPermissions,
                    Limits = _limits,
                }),
                TimeSpan.FromMilliseconds(_limits.StartupTimeoutMs)).ConfigureAwait(false);
            if (initResponse?.Error is { } initError)
            {
                throw new InvalidOperationException($"Worker initialization failed: {initError.Message}");
            }

            Transition(PluginRuntimeState.Activating, null);
            PluginWireMessage? activationResponse = await SendRequestAsync(PluginOps.PluginActivate, null, TimeSpan.FromMilliseconds(_limits.ActivationTimeoutMs)).ConfigureAwait(false);
            if (activationResponse?.Error is { } activationError)
            {
                throw new InvalidOperationException($"Activation failed: {activationError.Message}");
            }

            PluginPublishedActivation publication = ParseActivation(activationResponse?.Data);
            _published = publication;
            _environment.PublishActivation(publication);
            _faultNotified = false;
            _watchdog = new Timer(_ => WatchdogTick(), null, 500, 500);
            Transition(PluginRuntimeState.Active, null);
            _diagnostics.Write(PluginLogLevel.Info, $"Activated {_manifest.Id} {_manifest.Version} pid={_worker.ProcessId}.");
            return true;
        }
        catch (Exception exception)
        {
            await FaultAsync($"启动失败 / Startup failure: {exception.Message}", exitConfirmed: true).ConfigureAwait(false);
            return false;
        }
    }

    private PluginRegistryEntry EnsureEntry(PluginRegistryData data)
    {
        if (!data.Plugins.TryGetValue(_manifest.Id, out PluginRegistryEntry? entry))
        {
            entry = new PluginRegistryEntry
            {
                Id = _manifest.Id,
                SelectedVersion = _manifest.Version,
                ApprovedPermissions = [.. _manifest.Permissions],
            };
            data.Plugins[_manifest.Id] = entry;
        }

        return entry;
    }

    private PluginPublishedActivation ParseActivation(JsonElement? data)
    {
        List<MenuContribution> menus = [.. Enumerate(data, "menus", item => item.Deserialize<MenuContribution>(DtoJson.Options) ?? new())];
        List<SettingsPanelContribution> panels = [.. Enumerate(data, "settingsPanels", item => item.Deserialize<SettingsPanelContribution>(DtoJson.Options) ?? new())];
        List<ToolPageContribution> pages = [.. Enumerate(data, "toolPages", item => item.Deserialize<ToolPageContribution>(DtoJson.Options) ?? new())];
        List<BackgroundImageContribution> backgrounds = [.. Enumerate(data, "backgrounds", item => item.Deserialize<BackgroundImageContribution>(DtoJson.Options) ?? new())];
        return new PluginPublishedActivation
        {
            PluginId = _manifest.Id,
            PluginName = _manifest.Name,
            Version = _manifest.Version,
            ActivationId = _activationId,
            Menus = menus,
            SettingsPanels = panels,
            ToolPages = pages,
            Backgrounds = backgrounds,
        };
    }

    private static IEnumerable<T> Enumerate<T>(JsonElement? data, string property, Func<JsonElement, T> select)
    {
        if (data is not null && data.Value.TryGetProperty(property, out JsonElement element) && element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                yield return select(item);
            }
        }
    }

    public async Task StopAsync()
    {
        if (_state is not (PluginRuntimeState.Active or PluginRuntimeState.Activating or PluginRuntimeState.Starting))
        {
            return;
        }

        Transition(PluginRuntimeState.Stopping, "user stop");
        RevokeImmediately("stop");

        try
        {
            await SendRequestAsync(PluginOps.PluginDeactivate, null, TimeSpan.FromMilliseconds(_limits.StopGraceMs)).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }

        bool exited = await TerminateWorkerAsync().ConfigureAwait(false);
        if (!exited)
        {
            await FaultAsync("正常停止后未确认 worker 退出 / worker exit was not confirmed after stop", exitConfirmed: false).ConfigureAwait(false);
            return;
        }

        MarkAttemptEnded(clean: true);
        Transition(PluginRuntimeState.Disabled, "user stop");
    }

    public async Task StopForBudgetAsync(PluginBudgetSample sample)
    {
        if (_state is not (PluginRuntimeState.Active or PluginRuntimeState.Activating))
        {
            return;
        }

        Transition(PluginRuntimeState.Stopping, "budget protection");
        RevokeImmediately("budget");
        await TerminateWorkerAsync().ConfigureAwait(false);
        Transition(PluginRuntimeState.StoppedByBudget, "total budget protection");
        _diagnostics.Write(PluginLogLevel.Warning, $"Stopped to protect the tool-wide memory budget. Host={sample.HostPrivateBytes} Total={sample.TotalPrivateBytes}");
        FaultNotice?.Invoke(new HostFaultNotice
        {
            PluginId = _manifest.Id,
            PluginName = _manifest.Name,
            Version = _manifest.Version,
            Reason = "total-budget-protection",
            ActivationId = _activationId,
            ExitConfirmed = true,
            BudgetProtective = true,
        });
    }

    public async Task FaultAsync(string reason, bool exitConfirmed)
    {
        if (Interlocked.Exchange(ref _faultHandling, 1) == 1)
        {
            return;
        }

        try
        {
            if (_state is PluginRuntimeState.FaultDisabled)
            {
                return;
            }

            Transition(PluginRuntimeState.Stopping, reason);
            RevokeImmediately(reason);
            bool terminated = await TerminateWorkerAsync();
            exitConfirmed &= terminated;
            MarkAttemptEnded(clean: false);

            _registry.Mutate(data =>
            {
                PluginRegistryEntry entry = EnsureEntry(data);
                data.Plugins[_manifest.Id] = entry with
                {
                    FaultDisabled = new FaultDisableRecord
                    {
                        Version = _manifest.Version,
                        Digest = _digest,
                        Reason = reason,
                        ActivationId = _activationId,
                        FaultAtUtc = DateTime.UtcNow,
                        Notified = false,
                        ExitConfirmed = exitConfirmed,
                    },
                };
                if (!data.PendingNotices.Any(notice => notice.PluginId == _manifest.Id && notice.ActivationId == _activationId))
                {
                    data.PendingNotices.Add(new PendingNoticeRecord
                    {
                        PluginId = _manifest.Id,
                        Version = _manifest.Version,
                        Reason = reason,
                        ActivationId = _activationId,
                        ExitConfirmed = exitConfirmed,
                        Kind = NoticeKinds.Fault,
                    });
                }

                return data;
            });

            Transition(PluginRuntimeState.FaultDisabled, reason);
            _diagnostics.Write(PluginLogLevel.Error, $"Fault disabled: {reason} (exitConfirmed={exitConfirmed})");
            if (!_faultNotified)
            {
                _faultNotified = true;
                FaultNotice?.Invoke(new HostFaultNotice
                {
                    PluginId = _manifest.Id,
                    PluginName = _manifest.Name,
                    Version = _manifest.Version,
                    Reason = reason,
                    ActivationId = _activationId,
                    ExitConfirmed = exitConfirmed,
                    BudgetProtective = false,
                });
            }
        }
        finally
        {
            Interlocked.Exchange(ref _faultHandling, 0);
        }
    }

    private void RevokeImmediately(string reason)
    {
        _watchdog?.Dispose();
        _watchdog = null;
        if (_rawTap is not null)
        {
            _rawTap.Dispose();
            _rawTap = null;
        }

        _environment.SessionClosed -= OnEnvironmentSessionClosed;
        lock (_gate)
        {
            _serialSubscriptions.Clear();
        }

        _scope?.Revoke();
        _activationCancellation?.Cancel();
        if (_published is not null)
        {
            _environment.RemoveActivation(_manifest.Id);
            _published = null;
        }

        lock (_gate)
        {
            foreach (TaskCompletionSource<PluginWireMessage> pending in _pendingHostRequests.Values)
            {
                pending.TrySetException(new InvalidOperationException($"The activation stopped: {reason}."));
            }

            _pendingHostRequests.Clear();
            _workerRequestIds.Clear();
        }
    }

    private async Task<bool> TerminateWorkerAsync()
    {
        bool exited = true;
        if (_worker is { } worker)
        {
            _pipe?.Close();
            try
            {
                WindowsInterop.TerminateJobObject(worker.JobHandle, 1);
            }
            catch (Exception)
            {
            }

            int waitResult = WindowsInterop.WaitForSingleObject(worker.ProcessHandle, 5000);
            exited = waitResult == 0;
            _budget.UnregisterWorker(worker.ProcessId);
            WindowsInterop.CloseHandle(worker.ProcessHandle);
            WindowsInterop.CloseHandle(worker.JobHandle);
            _worker = null;
        }

        if (_pipe is not null)
        {
            await _pipe.DisposeAsync().ConfigureAwait(false);
            _pipe = null;
        }

        CleanupActivationTemp();
        return exited;
    }

    private void CleanupActivationTemp()
    {
        if (_activationId.Length == 0)
        {
            return;
        }

        try
        {
            foreach (string directory in new[]
            {
                Path.Combine(_paths.TempRoot, "WorkerScratch", _manifest.Id, _activationId),
                Path.Combine(_paths.TempRoot, "HostOutput", _manifest.Id, _activationId),
                Path.Combine(_paths.TempRoot, "HostSnapshots", _manifest.Id, _activationId),
            })
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch (Exception)
        {
        }
    }

    private void MarkAttemptEnded(bool clean)
    {
        string activationId = _activationId;
        _registry.Mutate(data =>
        {
            if (data.Plugins.TryGetValue(_manifest.Id, out PluginRegistryEntry? entry)
                && entry.LastAttempt is { } attempt
                && string.Equals(attempt.ActivationId, activationId, StringComparison.Ordinal))
            {
                data.Plugins[_manifest.Id] = entry with
                {
                    LastAttempt = attempt with { EndedCleanly = clean, EndedUtc = DateTime.UtcNow },
                };
            }

            return data;
        });
    }

    private void OnWorkerMessage(PluginWireMessage message)
    {
        if (!string.Equals(message.SessionId, _sessionId, StringComparison.Ordinal)
            || !string.Equals(message.ActivationId, _activationId, StringComparison.Ordinal))
        {
            _diagnostics.Write(PluginLogLevel.Warning, $"Dropped a stale message for activation '{message.ActivationId}'.");
            return;
        }

        switch (message.Kind)
        {
            case WireKinds.Response:
                lock (_gate)
                {
                    if (message.RequestId is { } requestId
                        && _pendingHostRequests.Remove(requestId, out TaskCompletionSource<PluginWireMessage>? waiter))
                    {
                        waiter.TrySetResult(message);
                    }
                }

                break;
            case WireKinds.Request:
                if (string.IsNullOrWhiteSpace(message.RequestId))
                {
                    _ = SendWorkerErrorAsync(message, PluginErrorCode.InvalidArgument, "A worker request must include a request id.");
                    break;
                }
                lock (_gate)
                {
                    if (!_workerRequestIds.Add(message.RequestId))
                    {
                        _ = SendWorkerErrorAsync(message, PluginErrorCode.InvalidArgument, "Duplicate worker request id.");
                        break;
                    }
                }
                if (!_workerRequestSlots.Wait(0))
                {
                    lock (_gate) _workerRequestIds.Remove(message.RequestId);
                    _ = SendWorkerErrorAsync(message, PluginErrorCode.ResourceLimit, "Too many worker requests are in progress.");
                    break;
                }
                _ = DispatchWorkerRequestAsync(message);
                break;
            case WireKinds.Notification:
                HandleWorkerNotification(message);
                break;
        }
    }

    private async Task DispatchWorkerRequestAsync(PluginWireMessage message)
    {
        PluginWireMessage response;
        try
        {
            using CancellationTokenSource deadline = new(GetBrokerTimeout(message.Operation));
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, _activationCancellation?.Token ?? CancellationToken.None);
            JsonElement? result = _broker is null
                ? throw new PluginScopeException(PluginErrorCode.SessionExpired, "The activation is not active.")
                : await _broker.ExecuteAsync(message.Operation, message.Data, linked.Token).ConfigureAwait(false);
            response = PluginWireMessage.Response(
                _sessionId,
                _activationId,
                message.RequestId ?? string.Empty,
                result is null ? JsonSerializer.SerializeToElement(new { ok = true }) : result);
        }
        catch (PluginScopeException exception)
        {
            response = PluginWireMessage.ErrorResponse(_sessionId, _activationId, message.RequestId ?? string.Empty, exception.Code, exception.Message);
        }
        catch (OperationCanceledException) when (_activationCancellation?.IsCancellationRequested == true)
        {
            response = PluginWireMessage.ErrorResponse(_sessionId, _activationId, message.RequestId ?? string.Empty, PluginErrorCode.SessionExpired, "The activation has been stopped.");
        }
        catch (OperationCanceledException)
        {
            response = PluginWireMessage.ErrorResponse(_sessionId, _activationId, message.RequestId ?? string.Empty, PluginErrorCode.DeadlineExceeded, "The broker operation exceeded its deadline.");
            _ = FaultAsync($"Worker request '{message.Operation}' exceeded the host deadline.", exitConfirmed: true);
        }
        catch (Exception exception)
        {
            response = PluginWireMessage.ErrorResponse(_sessionId, _activationId, message.RequestId ?? string.Empty, PluginErrorCode.InternalError, exception.Message);
        }

        if (_pipe is not null)
        {
            try
            {
                await _pipe.SendControlMessageAsync(response, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }
        lock (_gate)
        {
            if (message.RequestId is not null) _workerRequestIds.Remove(message.RequestId);
        }
        _workerRequestSlots.Release();
    }

    private TimeSpan GetBrokerTimeout(string operation) => operation switch
    {
        PluginOps.FilesPickRead or PluginOps.FilesPickWrite => TimeSpan.FromMinutes(10),
        PluginOps.LogsSnapshot => TimeSpan.FromMinutes(2),
        PluginOps.OutputWrite => TimeSpan.FromSeconds(30),
        PluginOps.OutputCommit => TimeSpan.FromMinutes(5),
        _ => TimeSpan.FromMilliseconds(_limits.CallTimeoutMs),
    };

    private async Task SendWorkerErrorAsync(PluginWireMessage message, string code, string text)
    {
        try
        {
            if (_pipe is not null)
                await _pipe.SendControlMessageAsync(PluginWireMessage.ErrorResponse(_sessionId, _activationId, message.RequestId ?? string.Empty, code, text), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }

    private void HandleWorkerNotification(PluginWireMessage message)
    {
        switch (message.Operation)
        {
            case PluginOps.WorkerHeartbeat:
                _lastHeartbeatUtc = DateTimeOffset.UtcNow;
                break;
            case PluginOps.LogDiag:
                string level = message.Data?.TryGetProperty("level", out JsonElement levelElement) == true ? levelElement.GetString() ?? "info" : "info";
                string text = message.Data?.TryGetProperty("message", out JsonElement textElement) == true ? textElement.GetString() ?? string.Empty : string.Empty;
                _broker?.WriteDiagnostic(level, text);
                break;
            case PluginOps.TaskProgress:
                string taskId = message.Data?.TryGetProperty("taskId", out JsonElement taskElement) == true ? taskElement.GetString() ?? string.Empty : string.Empty;
                int? percent = message.Data?.TryGetProperty("percent", out JsonElement percentElement) == true && percentElement.ValueKind == JsonValueKind.Number ? percentElement.GetInt32() : null;
                _diagnostics.Write(PluginLogLevel.Info, $"task '{taskId}' progress {(percent.HasValue ? percent.Value.ToString() + "%" : "tick")}");
                break;
            default:
                _diagnostics.Write(PluginLogLevel.Warning, $"Unknown notification '{message.Operation}' ignored.");
                break;
        }
    }

    internal void RegisterSerialSubscription(string subscriptionId, string sessionId)
    {
        lock (_gate)
        {
            _serialSubscriptions[subscriptionId] = new SerialSubscriptionState
            {
                SubscriptionId = subscriptionId,
                SessionId = sessionId,
            };
        }
    }

    internal void RemoveSerialSubscription(string subscriptionId)
    {
        lock (_gate)
        {
            _serialSubscriptions.Remove(subscriptionId);
        }
    }

    private void OnConnectionClosed(Exception? failure)
    {
        if (_state is PluginRuntimeState.Active or PluginRuntimeState.Activating or PluginRuntimeState.Starting)
        {
            string reason = failure switch
            {
                PipeRateViolationException => $"IPC 洪泛 / IPC flooding: {failure.Message}",
                PipeProtocolException => $"协议违例 / Protocol violation: {failure.Message}",
                _ => "进程退出或 IPC 断开 / process exit or IPC loss",
            };
            _ = FaultAsync(reason, exitConfirmed: false);
        }
    }

    private void WatchdogTick()
    {
        try
        {
            if (_state is not (PluginRuntimeState.Active or PluginRuntimeState.Activating))
            {
                return;
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            if ((now - _lastHeartbeatUtc).TotalMilliseconds > _limits.HeartbeatIntervalMs * _limits.HeartbeatMissLimit)
            {
                _ = FaultAsync($"无响应（连续 {_limits.HeartbeatMissLimit} 次心跳缺失）/ unresponsive", exitConfirmed: false);
                return;
            }

            long windowStart = now.ToUnixTimeMilliseconds() - _limits.SerialSustainedDropWindowMs;
            List<SerialSubscriptionState> laggards = [];
            lock (_gate)
            {
                foreach (SerialSubscriptionState subscription in _serialSubscriptions.Values)
                {
                    while (subscription.Window.Count > 0 && subscription.Window.Peek().Timestamp < windowStart)
                    {
                        subscription.Window.Dequeue();
                    }

                    if (subscription.Window.Count >= 20)
                    {
                        long delivered = subscription.Window.Count(entry => entry.Delivered);
                        if (delivered / (double)subscription.Window.Count < 1d - _limits.SerialSustainedDropThreshold)
                        {
                            laggards.Add(subscription);
                        }
                    }
                }
            }

            if (laggards.Count > 0)
            {
                string detail = laggards.Count == 1 ? $"subscription {laggards[0].SubscriptionId}" : $"{laggards.Count} subscriptions";
                _ = FaultAsync($"持续落后（{detail} 超过丢弃阈值）/ sustained receive backlog", exitConfirmed: false);
            }
        }
        catch (Exception)
        {
        }
    }

    private void OnEnvironmentSessionClosed(object? sender, string sessionId)
    {
        if (_pipe is null || _state != PluginRuntimeState.Active)
        {
            return;
        }

        PluginWireMessage notice = PluginWireMessage.Notify(
            _sessionId,
            _activationId,
            PluginOps.SessionClosed,
            JsonSerializer.SerializeToElement(new SessionClosedNotice { SessionId = sessionId }));
        _ = _pipe.SendControlMessageAsync(notice, CancellationToken.None);
    }

    private void OnRawBlock(string sessionId, ReadOnlyMemory<byte> data, DateTimeOffset receivedAtUtc)
    {
        if (_state != PluginRuntimeState.Active || _pipe is null)
        {
            return;
        }

        SerialSubscriptionState[] subscriptions;
        lock (_gate)
        {
            if (_serialSubscriptions.Count == 0)
            {
                return;
            }

            subscriptions = [.. _serialSubscriptions.Values.Where(subscription => string.Equals(subscription.SessionId, sessionId, StringComparison.Ordinal))];
        }

        if (subscriptions.Length == 0)
        {
            return;
        }

        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        string b64 = Convert.ToBase64String(data.Span);
        foreach (SerialSubscriptionState subscription in subscriptions)
        {
            long sequence = subscription.NextSequence++;
            SerialDataNotice notice = new()
            {
                SubscriptionId = subscription.SubscriptionId,
                SessionId = sessionId,
                Sequence = sequence,
                ByteOffset = 0,
                ReceivedAtUtc = receivedAtUtc,
                B64 = b64,
                GapFrom = subscription.GapFrom,
            };
            subscription.GapFrom = null;

            byte[] frame = PluginWire.Encode(PluginWireMessage.Notify(_sessionId, _activationId, PluginOps.SerialData, JsonSerializer.SerializeToElement(notice)));
            bool delivered = _pipe.TryEnqueueEventFrame(frame);
            lock (_gate)
            {
                subscription.Window.Enqueue((now, delivered));
                if (!delivered)
                {
                    subscription.GapFrom = sequence;
                    subscription.DroppedBlocks++;
                }
            }

            if (!delivered)
            {
                _diagnostics.Write(PluginLogLevel.Warning, $"Receive side channel dropped block seq={sequence} (queue full).");
            }
        }
    }

    public async Task<PluginWireMessage?> SendRequestAsync(string operation, JsonElement? data, TimeSpan timeout)
    {
        if (_pipe is null)
        {
            throw new InvalidOperationException("The worker is not connected.");
        }

        string requestId = $"h{Interlocked.Increment(ref _hostRequestCounter)}";
        PluginWireMessage request = PluginWireMessage.Request(_sessionId, _activationId, requestId, operation, data);
        TaskCompletionSource<PluginWireMessage> waiter = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _pendingHostRequests[requestId] = waiter;
        }

        try
        {
            await _pipe.SendControlMessageAsync(request, CancellationToken.None).ConfigureAwait(false);
            PluginWireMessage response = await waiter.Task.WaitAsync(timeout).ConfigureAwait(false);
            return response;
        }
        finally
        {
            lock (_gate)
            {
                _pendingHostRequests.Remove(requestId);
            }
        }
    }

    public async Task<bool> InvokeCommandAsync(string commandId, IReadOnlyDictionary<string, string>? values = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(commandId);
        if (_state != PluginRuntimeState.Active || _pipe is null)
        {
            return false;
        }

        PluginWireMessage? response = await SendRequestAsync(
            PluginOps.CommandInvoke,
            JsonSerializer.SerializeToElement(new CommandInvokeNotice { CommandId = commandId, Values = values ?? new Dictionary<string, string>() }),
            TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        return response?.Error is null;
    }

    public async Task<DuCom.Plugin.SettingsApplyOutcome?> ApplySettingsAsync(IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (_state != PluginRuntimeState.Active || _pipe is null)
        {
            return null;
        }

        PluginWireMessage? response = await SendRequestAsync(
            PluginOps.SettingsApply,
            JsonSerializer.SerializeToElement(new SettingsApplyNotice { Values = values }),
            TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        return response?.Data is null ? null : System.Text.Json.JsonSerializer.Deserialize<DuCom.Plugin.SettingsApplyOutcome>(response.Data.Value, DtoJson.Options);
    }

    public async ValueTask DisposeAsync()
    {
        RevokeImmediately("dispose");
        await TerminateWorkerAsync().ConfigureAwait(false);
        _activationCancellation?.Dispose();
        _workerRequestSlots.Dispose();
    }
}


