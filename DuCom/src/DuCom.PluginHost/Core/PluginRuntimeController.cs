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

public sealed partial class PluginRuntimeController : IAsyncDisposable
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

    public async Task<bool> NotifyPriorityCommandAsync(string commandId, IReadOnlyDictionary<string, string>? values = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(commandId);
        if (_state != PluginRuntimeState.Active || _pipe is null) return false;
        PluginWireMessage notification = PluginWireMessage.Notify(
            _sessionId,
            _activationId,
            PluginOps.CommandPriority,
            JsonSerializer.SerializeToElement(new CommandInvokeNotice { CommandId = commandId, Values = values ?? new Dictionary<string, string>() }));
        await _pipe.SendControlMessageAsync(notification, CancellationToken.None).ConfigureAwait(false);
        return true;
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


