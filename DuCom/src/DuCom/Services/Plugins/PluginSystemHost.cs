using System.IO;
using System.Reflection;
using System.Diagnostics;
using DuCom.PluginHost;
using DuCom.PluginHost.Core;
using DuCom.ViewModels;

namespace DuCom.Services.Plugins;

/// <summary>
/// Application composition for the DPP/1 plugin runtime: environment services, UI
/// dispatcher, background host, remembered grants, factory packs embedded in this
/// executable, configuration migration, budget settings, and the apply-and-restart flow.
/// </summary>
public sealed class PluginSystemHost : IAsyncDisposable
{
    private const long MemoryPressureEnterBytes = 800L * 1024 * 1024;
    private const long MemoryPressureExitBytes = 550L * 1024 * 1024;
    private const long MemoryPressureMaintenanceBytes = 650L * 1024 * 1024;
    private static readonly TimeSpan MemoryPressureMaintenanceInterval = TimeSpan.FromSeconds(30);
    private readonly PluginSystemService _service;
    private readonly DuComPluginHostEnvironment _environment;
    private readonly RememberedGrantsStore _rememberedGrants;
    private bool _memoryPressureActive;
    private long _nextMemoryPressureMaintenanceTimestamp;

    public PluginSystemHost(Func<IEnumerable<SessionViewModel>> sessionsProvider, Func<IEnumerable<PortItemViewModel>> portsProvider, SerialLeaseCoordinator serialLeases, BudgetGovernorConfig? budgetConfig = null, Func<string>? logDirectoryProvider = null)
    {
        ArgumentNullException.ThrowIfNull(sessionsProvider);
        Ui = new PluginUiDispatcher();
        Background = new BackgroundImageHostService();
        _rememberedGrants = RememberedGrantsStore.CreateDefault();
        _environment = new DuComPluginHostEnvironment(sessionsProvider, portsProvider, serialLeases, Ui, Background, _rememberedGrants, logDirectoryProvider);
        string executable = System.Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot determine the host executable for plugin workers.");
        _service = new PluginSystemService(
            PluginHostPaths.CreateDefault(),
            _environment,
            executable,
            budgetConfig);
        _service.ProgramLog += message => Program.DiagnosticLog?.Information(message);
        _service.Budget.Sampled += OnBudgetSampled;
        PluginHost.Diagnostics.PluginHostTrace.Sink = (level, message, exception) =>
        {
            switch (level)
            {
                case PluginHost.Diagnostics.PluginLogLevel.Warning:
                    Program.DiagnosticLog?.Warning(message, exception);
                    break;
                case PluginHost.Diagnostics.PluginLogLevel.Error:
                    Program.DiagnosticLog?.Error(message, exception);
                    break;
                default:
                    Program.DiagnosticLog?.Information(message);
                    break;
            }
        };
        Program.DiagnosticLog?.Information($"Plugin host version source. ApplicationVersion={PluginHostVersion.ApplicationVersion}; CompatibilityVersion={PluginHostVersion.CompatibilityVersion}; Comparison=DPP SemVer major.minor.patch only.");
    }


    public PluginSystemService Service => _service;

    public PluginUiDispatcher Ui { get; }

    public BackgroundImageHostService Background { get; }

    public DuComPluginHostEnvironment Environment => _environment;

    public event Action<bool, long>? MemoryPressureChanged;

    public event Action<PluginBudgetSample>? MemoryPressureMaintenanceRequested;

    private void OnBudgetSampled(PluginBudgetSample sample)
    {
        bool next = _memoryPressureActive
            ? sample.TotalPrivateBytes >= MemoryPressureExitBytes
            : sample.TotalPrivateBytes >= MemoryPressureEnterBytes;
        if (next == _memoryPressureActive)
        {
            if (next && sample.TotalPrivateBytes >= MemoryPressureMaintenanceBytes)
            {
                long now = Stopwatch.GetTimestamp();
                long nextMaintenance = Volatile.Read(ref _nextMemoryPressureMaintenanceTimestamp);
                if (now >= nextMaintenance && Interlocked.CompareExchange(
                        ref _nextMemoryPressureMaintenanceTimestamp,
                        now + (long)(MemoryPressureMaintenanceInterval.TotalSeconds * Stopwatch.Frequency),
                        nextMaintenance) == nextMaintenance)
                {
                    MemoryPressureMaintenanceRequested?.Invoke(sample);
                }
            }
            return;
        }

        _memoryPressureActive = next;
        Volatile.Write(
            ref _nextMemoryPressureMaintenanceTimestamp,
            Stopwatch.GetTimestamp() + (long)(MemoryPressureMaintenanceInterval.TotalSeconds * Stopwatch.Frequency));
        MemoryPressureChanged?.Invoke(next, sample.TotalPrivateBytes);
    }

    public async Task InitializeAsync(string legacySettingsJson, string? legacyLogPackagePreferencesJson)
    {
        PluginConfigMigrator migrator = new(
            Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                "DuCom", "Plugins", "Data"),
            _rememberedGrants);
        migrator.MigrateLegacyBuiltIns(legacySettingsJson, legacyLogPackagePreferencesJson);
        await _service.InitializeAsync(BuildFactoryPacks());
    }

    public static IReadOnlyList<FactoryPackDefinition> BuildFactoryPacks()
    {
        Assembly assembly = typeof(PluginSystemHost).Assembly;
        const string Prefix = "DuCom.Factory.";
        List<FactoryPackDefinition> packs = [];
        foreach (string resource in assembly.GetManifestResourceNames().Where(name => name.StartsWith(Prefix, StringComparison.Ordinal)).OrderBy(name => name, StringComparer.Ordinal))
        {
            string rest = resource[Prefix.Length..];
            int versionSeparator = rest.IndexOf('/');
            if (versionSeparator <= 0)
            {
                continue;
            }

            string pluginId = rest[..versionSeparator];
            string remainder = rest[(versionSeparator + 1)..];
            int pathSeparator = remainder.IndexOf('/');
            if (pathSeparator <= 0)
            {
                continue;
            }

            string version = remainder[..pathSeparator];
            string relativePath = remainder[(pathSeparator + 1)..].Replace('/', Path.DirectorySeparatorChar);
            FactoryPackDefinition? pack = packs.FirstOrDefault(candidate => candidate.PluginId == pluginId && candidate.Version == version);
            if (pack is null)
            {
                pack = new FactoryPackDefinition { PluginId = pluginId, Version = version, Files = [] };
                packs.Add(pack);
            }

            string local = relativePath;
            pack.Files.Add(FactoryPackFile.FromStream(NormalizeKey(local), () =>
                assembly.GetManifestResourceStream(resource)
                    ?? throw new InvalidOperationException($"Embedded factory resource '{resource}' disappeared.")));
        }

        return packs;
    }

    private static string NormalizeKey(string path) => path.Replace(Path.DirectorySeparatorChar, '/');

    public async Task<(bool Succeeded, string Message)> ApplyAndRestartAsync(
        Func<Task<bool>> closeSessionsAsync,
        Func<Task> saveSettingsAsync,
        Func<Task> restart)
    {
        IReadOnlyList<PluginManagerRow> pending = _service.BuildManagerRows().Where(row => row.PendingUpdate).ToList();
        if (pending.Count == 0)
        {
            return (false, "没有待应用更改 / No pending plugin changes.");
        }

        if (!await closeSessionsAsync())
        {
            return (false, "串口会话未关闭，已取消重启 / Serial sessions were not closed; restart cancelled.");
        }

        try
        {
            await saveSettingsAsync();
        }
        catch (Exception exception)
        {
            Program.DiagnosticLog?.Error("Settings save failed before plugin apply-and-restart.", exception);
            return (false, $"保存设置失败，未重启 / Settings were not saved: {exception.Message}");
        }

        try
        {
            await _service.StopAllAsync();
        }
        catch (Exception exception)
        {
            Program.DiagnosticLog?.Error("Plugin stop failed before apply-and-restart.", exception);
            return (false, $"停止插件失败，未重启 / Plugins were not stopped: {exception.Message}");
        }

        try
        {
            await restart();
            return (true, string.Empty);
        }
        catch (Exception exception)
        {
            Program.DiagnosticLog?.Error("Replacement DuCom process could not be started; current application remains open.", exception);
            return (false, $"无法启动新的 DuCom 进程，当前程序仍在运行 / Could not start the replacement process: {exception.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _service.Budget.Sampled -= OnBudgetSampled;
        await _service.DisposeAsync();
    }
}
