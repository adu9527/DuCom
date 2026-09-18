using System.Text.Json;
using System.Diagnostics;
using DuCom.Plugin;
using DuCom.PluginHost.Core;
using DuCom.PluginHost.Diagnostics;
using DuCom.PluginHost.Packages;
using DuCom.PluginHost.Registry;
using DuCom.PluginHost.Security;

namespace DuCom.PluginHost;

public sealed record FactoryPackFile(string RelativePath, Func<byte[]> Content)
{
    private Func<Stream>? _openStream;
    private Func<byte[]>? _streamContent;

    /// <summary>Creates a file whose factory returns a fresh readable stream owned by the caller.</summary>
    public static FactoryPackFile FromStream(string relativePath, Func<Stream> openStream)
    {
        ArgumentNullException.ThrowIfNull(openStream);
        Func<byte[]> content = () =>
        {
            using Stream source = openStream();
            using MemoryStream buffer = new();
            source.CopyTo(buffer);
            return buffer.ToArray();
        };
        return new FactoryPackFile(relativePath, content) { _openStream = openStream, _streamContent = content };
    }

    /// <summary>Opens file content; the caller must dispose the returned stream.</summary>
    public Stream OpenRead()
    {
        // Honor legacy record copies that replace Content using a with expression.
        return _openStream is not null && ReferenceEquals(Content, _streamContent)
            ? _openStream()
            : new MemoryStream(Content(), writable: false);
    }
}

public sealed record FactoryPackDefinition
{
    public required string PluginId { get; init; }
    public required string Version { get; init; }
    public required List<FactoryPackFile> Files { get; init; }
}

public sealed record PluginManagerRow
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Version { get; init; }
    public required string Source { get; init; }
    public required PluginRuntimeState State { get; init; }
    public required IReadOnlyList<string> Capabilities { get; init; }
    public required IReadOnlyList<string> Permissions { get; init; }
    public required string FaultReason { get; init; }
    public required bool PendingUpdate { get; init; }
    public required string PendingUpdateVersion { get; init; }
    public required uint WorkerPid { get; init; }
    public required long WorkerPrivateBytes { get; init; }
    public required bool Enabled { get; init; }

    public bool HasFaultReason => !string.IsNullOrWhiteSpace(FaultReason);

    public bool CanRetry => State is PluginRuntimeState.FaultDisabled or PluginRuntimeState.Rejected;

    public string ToggleLabel => State is PluginRuntimeState.Active or PluginRuntimeState.Activating ? "停用 / Disable" : "启用 / Enable";

    public bool IsActive => State is PluginRuntimeState.Active or PluginRuntimeState.Activating;

    public bool IsInactive => !IsActive;

    public bool IsDisabled => !Enabled;

    public bool IsBuiltIn => string.Equals(Source, "BuiltIn", StringComparison.Ordinal);

    public string Description => Id switch
    {
        "com.ducom.background-image" => "在 DuCom 主窗口底层显示自定义图片，支持单图和目录轮播。",
        "com.ducom.log-package" => "选择当前串口会话日志，填写问题信息并生成 ZIP 压缩包。",
        "com.ducom.timer" => "秒表计时器：正计时、计次分段对比、记录导出，工具窗口支持置顶。",
        _ => "通过 DuCom 插件运行时提供扩展功能。",
    };
}

/// <summary>
/// Top-level plugin runtime facade: registry-backed discovery, factory pack materialization,
/// installation, enable/disable/retry/uninstall, worker lifecycle, and budget governance.
/// The host application owns the environment services; this type never touches UI types.
/// </summary>
public sealed partial class PluginSystemService : IAsyncDisposable
{
    private readonly PluginHostPaths _paths;
    private readonly IPluginHostEnvironment _environment;
    private readonly SandboxLauncher _launcher;
    private readonly PluginRegistryStore _registry;
    private readonly PluginPackageInstaller _installer;
    private readonly BudgetGovernor _budget;
    private readonly HostTempDiskBudget _hostTempDiskBudget;
    private readonly HostTempResourceLedger _hostTempLedger;
    private readonly Dictionary<string, PluginRuntimeController> _controllers = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _startupRejections = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly HashSet<string> _budgetRecoveryPending = new(StringComparer.Ordinal);
    private readonly string _hostExecutablePath;
    private readonly Func<string, bool> _isOfficialNamespace;
    private long _lastBudgetWarningLogTimestamp;
    private long _lastBudgetWarningLoggedBytes;
    private bool _initialized;

    public PluginSystemService(
        PluginHostPaths paths,
        IPluginHostEnvironment environment,
        string hostExecutablePath,
        BudgetGovernorConfig? budgetConfig = null,
        Func<string, bool>? isOfficialNamespace = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        ArgumentException.ThrowIfNullOrEmpty(hostExecutablePath);
        _hostExecutablePath = hostExecutablePath;
        _launcher = new SandboxLauncher(hostExecutablePath);
        _registry = new PluginRegistryStore(paths.RegistryPath);
        _installer = new PluginPackageInstaller(paths.InstalledRoot);
        _budget = new BudgetGovernor(budgetConfig ?? new BudgetGovernorConfig());
        _hostTempDiskBudget = new HostTempDiskBudget(_budget.Configuration.HostTempDiskQuotaBytes);
        _hostTempLedger = new HostTempResourceLedger(paths.TempRoot, _registry.HostRunId, _hostTempDiskBudget);
        _isOfficialNamespace = isOfficialNamespace ?? (id => id.StartsWith("com.ducom.", StringComparison.Ordinal) && FactoryIds.Contains(id));
        _budget.Warning += LogBudgetWarning;
        _budget.EmergencyStop += request => _ = HandleBudgetStopAsync(request);
        // Budget stops are protective, not faults: once memory recovers below the warning
        // line, quietly restart the plugins that were stopped so features come back alive
        // without asking the user to babysit the restart button.
        _budget.Sampled += OnBudgetSampled;
        _budget.RestrictionRecovered += sample => ScheduleBudgetRecovery();
        _budget.Recovered += LogBudgetRecovered;
    }

    public static readonly IReadOnlyList<string> FactoryIds =
    [
        "com.ducom.background-image",
        "com.ducom.log-package",
        "com.ducom.timer",
    ];

    public event Action<string>? ProgramLog;

    public event Action? Changed;

    public event Action<HostFaultNotice>? FaultNotice;

    public PluginRegistryStore Registry => _registry;

    public BudgetGovernor Budget => _budget;

    public PluginHostPaths Paths => _paths;

    public string DiagnosticsRoot => _paths.DiagnosticsRoot;

    public bool SafeStartAllPlugins
    {
        get => _registry.Current.SafeStartAllPlugins;
        set
        {
            _registry.Mutate(data => data with { SafeStartAllPlugins = value });
            Changed?.Invoke();
        }
    }

    private void LogBudgetWarning(PluginBudgetSample sample)
    {
        const long minimumGrowthBytes = 32L * 1024 * 1024;
        long now = Stopwatch.GetTimestamp();
        long previousTimestamp = Volatile.Read(ref _lastBudgetWarningLogTimestamp);
        long previousBytes = Volatile.Read(ref _lastBudgetWarningLoggedBytes);
        if (previousTimestamp != 0 &&
            now - previousTimestamp < 30L * Stopwatch.Frequency &&
            sample.TotalPrivateBytes - previousBytes < minimumGrowthBytes)
        {
            return;
        }

        Volatile.Write(ref _lastBudgetWarningLogTimestamp, now);
        Volatile.Write(ref _lastBudgetWarningLoggedBytes, sample.TotalPrivateBytes);
        ProgramLog?.Invoke(FormatBudgetSample("Plugin budget warning", sample));
    }

    private void LogBudgetRecovered(PluginBudgetSample sample)
    {
        Volatile.Write(ref _lastBudgetWarningLogTimestamp, 0);
        Volatile.Write(ref _lastBudgetWarningLoggedBytes, 0);
        ProgramLog?.Invoke(FormatBudgetSample("Plugin budget recovered", sample));
    }

    private static string FormatBudgetSample(string prefix, PluginBudgetSample sample) =>
        $"{prefix}: total={sample.TotalPrivateBytes:N0} host={sample.HostPrivateBytes:N0} " +
        $"managed={sample.ManagedHeapBytes:N0} heap={sample.ManagedHeapSizeBytes:N0} fragmented={sample.ManagedFragmentedBytes:N0} " +
        $"threads={sample.ProcessThreadCount} poolThreads={sample.ThreadPoolThreadCount} pendingWork={sample.ThreadPoolPendingWorkItemCount:N0}";

    public async ValueTask DisposeAsync()
    {
        _budget.Sampled -= OnBudgetSampled;
        await CancelAutoRestartsAsync().ConfigureAwait(false);
        await StopAllAsync().ConfigureAwait(false);
        _budget.Dispose();
        _launcher.Dispose();
        _hostTempLedger.Dispose();
    }
}
