using System.Text.Json;
using DuCom.Plugin;
using DuCom.Plugin.Dto;
using DuCom.PluginHost.Diagnostics;
using DuCom.PluginHost.Registry;
using DuCom.PluginHost.Security;
using DuCom.PluginHost.Transport;

namespace DuCom.PluginHost.Core;

public sealed partial class PluginRuntimeController
{
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
            _broker = new PluginBroker(_scope, _environment, _diagnostics, _versionDirectory);
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

}
