using System.Diagnostics;
using System.IO;
using System.Windows;
using DuCom.PluginHost;
using DuCom.PluginHost.Core;
using DuCom.ViewModels;
using Microsoft.Win32;

namespace DuCom.Services.Plugins;

/// <summary>
/// Application-side implementation of the plugin host environment: bridges broker calls to
/// live serial sessions (raw tap fan-out, log snapshots), host-owned pickers on the UI
/// thread, remembered read grants, declarative UI publication, and background rendering.
/// </summary>
public sealed class DuComPluginHostEnvironment : IPluginHostEnvironment
{
    private readonly Func<IEnumerable<SessionViewModel>> _sessionsProvider;
    private readonly Func<IEnumerable<PortItemViewModel>> _portsProvider;
    private readonly SerialLeaseCoordinator _serialLeases;
    private readonly PluginUiDispatcher _ui;
    private readonly BackgroundImageHostService _background;
    private readonly RememberedGrantsStore _rememberedGrants;
    private readonly Func<string> _logDirectoryProvider;
    private readonly object _gate = new();
    private readonly Dictionary<string, IDisposable> _rawTapRegistrations = new(StringComparer.Ordinal);
    private readonly HashSet<string> _knownSessions = new(StringComparer.Ordinal);
    private readonly List<Action<string, ReadOnlyMemory<byte>, DateTimeOffset>> _rawHandlers = [];

    private static readonly TimeSpan RawHandlerFaultLogInterval = TimeSpan.FromSeconds(10);
    private long _rawHandlerFaultLogTimestamp;

    public DuComPluginHostEnvironment(
        Func<IEnumerable<SessionViewModel>> sessionsProvider,
        Func<IEnumerable<PortItemViewModel>> portsProvider,
        SerialLeaseCoordinator serialLeases,
        PluginUiDispatcher ui,
        BackgroundImageHostService background,
        RememberedGrantsStore rememberedGrants,
        Func<string>? logDirectoryProvider = null)
    {
        ArgumentNullException.ThrowIfNull(sessionsProvider);
        _sessionsProvider = sessionsProvider;
        _portsProvider = portsProvider;
        _serialLeases = serialLeases;
        _ui = ui;
        _background = background;
        _rememberedGrants = rememberedGrants;
        _logDirectoryProvider = logDirectoryProvider ?? DefaultLogDirectory;
    }

    private static string DefaultLogDirectory() => Path.Combine(AppContext.BaseDirectory, "Logs");

    // DPP manifests are SemVer (three numeric components). Do not truncate the application
    // assembly's four-part Windows version and accidentally turn 0.0.0.4 into 0.0.0.
    public string HostVersion => PluginHostVersion.CompatibilityVersion;

    public string Culture => System.Globalization.CultureInfo.CurrentUICulture.Name;

    public event EventHandler<string>? SessionClosed;

    public async Task<HostSerialLeaseResult> AcquireSerialLeaseAsync(HostSerialLeaseRequest request, CancellationToken cancellationToken)
    {
        return await _serialLeases.RunPortOperationAsync(request.Port, async () =>
        {
            PortItemViewModel? currentPort = _portsProvider().FirstOrDefault(item => string.Equals(item.PortName, request.Port, StringComparison.OrdinalIgnoreCase));
            if (request.DeviceIdentity is not null
                && (currentPort is null || !string.Equals(currentPort.DeviceInstanceId, request.DeviceIdentity, StringComparison.Ordinal)))
                return new HostSerialLeaseResult(string.Empty, request.Port, "changed", false, false, "The physical serial device changed after task confirmation.");
            SessionViewModel? session = _sessionsProvider().FirstOrDefault(item => string.Equals(item.PortName, request.Port, StringComparison.OrdinalIgnoreCase));
            bool wasOpen = session?.IsOpen == true;
            SerialLeaseSnapshot lease;
            try { lease = _serialLeases.Acquire(request.PluginId, request.ActivationId, request.TaskId, request.Port, request.DeviceIdentity, request.RestoreSession, wasOpen); }
            catch (InvalidOperationException exception) { return new HostSerialLeaseResult(string.Empty, request.Port, "busy", wasOpen, false, exception.Message); }
            try
            {
                if (wasOpen) await session!.CloseAsync(cancellationToken);
                return new HostSerialLeaseResult(lease.LeaseId, lease.Port, "leased", wasOpen, false, null);
            }
            catch
            {
                _serialLeases.Release(lease.LeaseId);
                throw;
            }
        });
    }

    public async Task<HostSerialLeaseResult> ReleaseSerialLeaseAsync(string pluginId, string activationId, string leaseId, CancellationToken cancellationToken)
    {
        SerialLeaseSnapshot lease = _serialLeases.GetOwned(pluginId, activationId, leaseId);
        bool restored = false;
        string? message = null;
        SessionViewModel? session = _sessionsProvider().FirstOrDefault(item => string.Equals(item.PortName, lease.Port, StringComparison.OrdinalIgnoreCase));
        PortItemViewModel? currentPort = _portsProvider().FirstOrDefault(item => string.Equals(item.PortName, lease.Port, StringComparison.OrdinalIgnoreCase));
        bool identityMatches = lease.DeviceIdentity is not null
            && currentPort is not null
            && string.Equals(currentPort.DeviceInstanceId, lease.DeviceIdentity, StringComparison.Ordinal);
        if (lease.RestoreSession && lease.SessionWasOpen && session is not null && identityMatches)
        {
            try
            {
                await _serialLeases.RunAsOwnerAsync(lease.LeaseId, async () => restored = await session.OpenAsync(cancellationToken) == Core.Ports.PortCommandResult.Succeeded);
                if (!restored) message = "The previous DuCom session could not be restored.";
            }
            catch (Exception exception) { message = exception.Message; }
        }
        else if (lease.RestoreSession && lease.SessionWasOpen) message = "The previous DuCom session was not restored because physical device identity could not be confirmed.";
        _serialLeases.Release(lease.LeaseId);
        return new HostSerialLeaseResult(lease.LeaseId, lease.Port, restored ? "released-restored" : "released", lease.SessionWasOpen, restored, message);
    }

    public void RevokeSerialLeases(string pluginId, string activationId) => _serialLeases.Revoke(pluginId, activationId);

    public IReadOnlyList<HostSerialSession> GetSerialSessions()
    {
        List<HostSerialSession> sessions = [];
        HashSet<string> live = new(StringComparer.Ordinal);
        foreach (SessionViewModel viewModel in _sessionsProvider())
        {
            bool open = viewModel.WorkspaceSession.Status.State.State is Core.Ports.PortLifecycleState.Open or Core.Ports.PortLifecycleState.Opening;
            sessions.Add(new HostSerialSession(viewModel.WorkspaceSession.RuntimeId, viewModel.PortName, open));
            live.Add(viewModel.WorkspaceSession.RuntimeId);
            if (open)
            {
                EnsureRawTap(viewModel.WorkspaceSession.RuntimeId);
            }
        }

        DetectClosedSessions(live);
        return sessions;
    }

    public IReadOnlyList<HostSerialPort> GetSerialPorts() => [.. _portsProvider().Select(port => new HostSerialPort(
        port.PortName,
        string.IsNullOrWhiteSpace(port.DisplayDeviceName) ? port.PortName : port.DisplayDeviceName,
        port.VidPid,
        port.DeviceInstanceId))];

    public async Task<IReadOnlyList<HostLogSnapshot>> CreateLogSnapshotsAsync(string? sessionId, CancellationToken cancellationToken)
    {
        List<HostLogSnapshot> snapshots = [];
        foreach (SessionViewModel viewModel in _sessionsProvider())
        {
            if (sessionId is not null && !string.Equals(viewModel.WorkspaceSession.RuntimeId, sessionId, StringComparison.Ordinal))
            {
                continue;
            }

            Core.Logging.SessionLogFileSnapshot[] files;
            try
            {
                IReadOnlyList<Core.Logging.SessionLogFileSnapshot> result = await viewModel.WorkspaceSession.CreateLogSnapshotAsync(cancellationToken);
                files = [.. result];
            }
            catch (Exception exception)
            {
                Program.DiagnosticLog?.Warning(
                    $"Log snapshot failed for port '{viewModel.PortName}'; the session is skipped in this snapshot.",
                    exception);
                continue;
            }

            snapshots.Add(new HostLogSnapshot(
                viewModel.WorkspaceSession.RuntimeId,
                viewModel.PortName,
                [.. files.Select(file => new HostLogSnapshotFile(file.Path, file.Length, viewModel.PortName, Path.GetFileName(file.Path)))]));
        }

        return snapshots;
    }

    public IDisposable SubscribeRawBlocks(Action<string, ReadOnlyMemory<byte>, DateTimeOffset> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate)
        {
            _rawHandlers.Add(handler);
            foreach (SessionViewModel viewModel in _sessionsProvider())
            {
                if (viewModel.WorkspaceSession.Status.State.State is Core.Ports.PortLifecycleState.Open or Core.Ports.PortLifecycleState.Opening)
                {
                    EnsureRawTap(viewModel.WorkspaceSession.RuntimeId);
                }
            }
        }

        return new Subscription(() =>
        {
            lock (_gate)
            {
                _rawHandlers.Remove(handler);
            }
        });
    }

    private void EnsureRawTap(string runtimeId)
    {
        lock (_gate)
        {
            if (_rawTapRegistrations.ContainsKey(runtimeId))
            {
                return;
            }

            IWorkspaceSession? session = _sessionsProvider().FirstOrDefault(candidate => candidate.WorkspaceSession.RuntimeId == runtimeId)?.WorkspaceSession;
            if (session is null)
            {
                return;
            }

            _rawTapRegistrations[runtimeId] = session.RawTaps.Register(new Core.Sessions.SessionRawTap
            {
                Id = "plugin-broker",
                Publish = (bytes, receivedAtUtc) => FanOut(runtimeId, bytes, receivedAtUtc),
            });
        }
    }

    private void FanOut(string runtimeId, ReadOnlyMemory<byte> bytes, DateTimeOffset receivedAtUtc)
    {
        Action<string, ReadOnlyMemory<byte>, DateTimeOffset>[] handlers;
        lock (_gate)
        {
            if (_rawHandlers.Count == 0)
            {
                return;
            }

            handlers = [.. _rawHandlers];
        }

        foreach (Action<string, ReadOnlyMemory<byte>, DateTimeOffset> handler in handlers)
        {
            try
            {
                byte[] copy = bytes.ToArray();
                handler(runtimeId, copy, receivedAtUtc);
            }
            catch (Exception exception)
            {
                // A misbehaving handler must not break fan-out to the others. At high baud
                // rates a failing handler could produce thousands of faults per second, so
                // repeats are throttled to one logged line per interval.
                LogRawHandlerFaultThrottled(exception);
            }
        }
    }

    private void LogRawHandlerFaultThrottled(Exception exception)
    {
        long now = Stopwatch.GetTimestamp();
        long previous = Volatile.Read(ref _rawHandlerFaultLogTimestamp);
        if (now - previous < RawHandlerFaultLogInterval.Ticks
            || Interlocked.CompareExchange(ref _rawHandlerFaultLogTimestamp, now, previous) != previous)
        {
            return;
        }

        Program.DiagnosticLog?.Warning(
            "A plugin raw-data handler faulted; further faults are logged at most once per 10s.",
            exception);
    }

    private void DetectClosedSessions(HashSet<string> live)
    {
        List<string>? closed = null;
        lock (_gate)
        {
            foreach (string known in _knownSessions)
            {
                if (!live.Contains(known))
                {
                    (closed ??= []).Add(known);
                }
            }

            _knownSessions.Clear();
            foreach (string session in live)
            {
                _knownSessions.Add(session);
            }

            foreach (string removed in _rawTapRegistrations.Keys.Where(id => !live.Contains(id)).ToList())
            {
                if (_rawTapRegistrations.Remove(removed, out IDisposable? registration))
                {
                    registration.Dispose();
                }
            }
        }

        if (closed is not null)
        {
            foreach (string sessionId in closed)
            {
                SessionClosed?.Invoke(this, sessionId);
            }
        }
    }

    public Task<HostPickResult?> PickReadAsync(string pluginId, HostPickRequest request, CancellationToken cancellationToken)
    {
        bool remember = request.Remember;
        return RunOnUiAsync(() =>
        {
            string? path = request.Mode == "directory" ? PickFolder() : PickFile(request);
            if (path is null)
            {
                return null;
            }

            bool remembered = false;
            if (remember)
            {
                remembered = true;
            }

            return new HostPickResult
            {
                DisplayPath = path,
                IsDirectory = request.Mode == "directory",
                Remembered = remembered,
                Entries = [],
            };
        }).ContinueWith(task =>
        {
            HostPickResult? result = task.Result;
            if (result is not null && remember)
            {
                _rememberedGrants.Add(pluginId, result.DisplayPath);
            }

            return result;
        }, CancellationToken.None);
    }


    public Task<HostPickResult?> PickWriteAsync(string pluginId, HostPickRequest request, CancellationToken cancellationToken) =>
        RunOnUiAsync(() =>
        {
            if (string.Equals(request.Mode, "directory", StringComparison.Ordinal))
            {
                string? folder = PickFolder();
                return folder is null
                    ? null
                    : new HostPickResult
                    {
                        DisplayPath = folder,
                        IsDirectory = true,
                        Remembered = false,
                        Entries = [],
                    };
            }

            string? path = PickSaveFile(request);
            if (path is null) return null;
            bool replacePathApproved = false;
            if (File.Exists(path))
            {
                MessageBoxResult confirmation = MessageBox.Show(
                    $"The file '{Path.GetFileName(path)}' already exists. Replace it?",
                    "Confirm file replacement",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (confirmation != MessageBoxResult.Yes) return null;
                replacePathApproved = true;
            }
            return new HostPickResult
            {
                DisplayPath = path,
                IsDirectory = false,
                Remembered = false,
                Entries = [],
                ReplacePathApproved = replacePathApproved,
            };
        }).ContinueWith(task =>
        {
            HostPickResult? result = task.Result;
            // A directory the user picked as a write destination is remembered so later
            // createWriteTarget calls can resolve it without another dialog.
            if (result is not null && result.IsDirectory)
            {
                _rememberedGrants.Add(pluginId, result.DisplayPath);
            }

            return result;
        }, CancellationToken.None);


    private static Task<T?> RunOnUiAsync<T>(Func<T?> action)
    {
        Application? application = Application.Current;
        if (application is null)
        {
            return Task.FromResult<T?>(default);
        }

        return application.Dispatcher.InvokeAsync(action).Task;
    }

    private static string? PickFile(HostPickRequest request)
    {
        string filter = BuildFilter(request);
        OpenFileDialog dialog = new()
        {
            CheckFileExists = true,
            Filter = filter,
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private static string? PickFolder()
    {
        OpenFolderDialog dialog = new();
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    private static string? PickSaveFile(HostPickRequest request)
    {
        SaveFileDialog dialog = new()
        {
            FileName = string.IsNullOrWhiteSpace(request.SuggestName) ? "output.zip" : request.SuggestName,
            Filter = BuildFilter(request),
            OverwritePrompt = false,
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private static string BuildFilter(HostPickRequest request)
    {
        if (request.Extensions is { Count: > 0 })
        {
            string patterns = string.Join(";", request.Extensions.Select(ext => ext.StartsWith('.') ? "*" + ext : "*." + ext));
            return $"{(string.IsNullOrWhiteSpace(request.FilterName) ? "Files" : request.FilterName)}|{patterns}|All files|*.*";
        }

        return "All files|*.*";
    }

    public string? TryResolveRememberedReadPath(string pluginId, string requestedPath) => _rememberedGrants.Resolve(pluginId, requestedPath);

    public void ForgetRememberedReadPath(string pluginId, string path) => _rememberedGrants.Remove(pluginId, path);

    public string GetLogDirectory()
    {
        try
        {
            string? directory = _logDirectoryProvider();
            if (!string.IsNullOrWhiteSpace(directory))
            {
                return directory;
            }
        }
        catch (Exception exception)
        {
            Program.DiagnosticLog?.Warning(
                "The log directory provider failed; falling back to the base-directory Logs folder.",
                exception);
        }

        return Path.Combine(AppContext.BaseDirectory, "Logs");
    }

    public Task<bool> ShowNoticeAsync(HostPluginNotice notice)
    {
        ArgumentNullException.ThrowIfNull(notice);
        return RunOnUiAsync(() =>
        {
            Application? application = Application.Current;
            if (application is null || application.Dispatcher.HasShutdownStarted || application.Dispatcher.HasShutdownFinished)
            {
                return false;
            }

            Window? owner = application.Windows.OfType<PluginToolWindow>()
                               .FirstOrDefault(window => string.Equals(window.PluginId, notice.PluginId, StringComparison.Ordinal) && window.IsLoaded)
                           ?? (application.MainWindow is { IsVisible: true } main ? main : null);
            if (owner is null)
            {
                return false;
            }

            ThemedMessageDialog.ShowInfo(owner, notice.Message, notice.Title);
            if (!string.IsNullOrEmpty(notice.RevealPath) && File.Exists(notice.RevealPath))
            {
                try
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{notice.RevealPath}\"") { UseShellExecute = true });
                }
                catch (Exception)
                {
                }
            }

            return true;
        });
    }

    public Task<bool> ShowFaultNoticeAsync(HostFaultNotice notice) => _ui.ShowFaultNoticeAsync(notice);

    public void PublishActivation(PluginPublishedActivation activation) => _ui.PublishActivation(activation);

    public void UpdateToolPage(string pluginId, string contributionId, IReadOnlyList<Plugin.UiNode> nodes) => _ui.UpdateToolPage(pluginId, contributionId, nodes);

    public void RemoveActivation(string pluginId) => _ui.RemoveActivation(pluginId);

    public void ApplyBackground(BackgroundApply apply) => _background.Apply(apply);

    private sealed class Subscription(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
