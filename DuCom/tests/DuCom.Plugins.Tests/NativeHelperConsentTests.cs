using System.Text.Json;
using DuCom.Plugin;
using DuCom.Plugin.Dto;
using DuCom.PluginHost;
using DuCom.PluginHost.Core;
using DuCom.PluginHost.Registry;
using DuCom.PluginHost.Diagnostics;
using Xunit;

namespace DuCom.Plugins.Tests;

public sealed class NativeHelperConsentTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ducom-helper-consent-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task EnvironmentWithoutConfirmationImplementationFailsClosed()
    {
        IPluginHostEnvironment environment = new FakeEnvironment();
        Assert.False(await environment.ConfirmNativeHelperExecutionAsync(Request(), default));
    }

    [Fact]
    public async Task ManifestPermissionAloneCannotLaunchHelperThroughBroker()
    {
        PluginManifest manifest = Manifest();
        using ActivationScope scope = new(manifest, "activation-1", Path.Combine(_root, "storage"),
            Path.Combine(_root, "scratch"), Path.Combine(_root, "output"), Path.Combine(_root, "snapshots"),
            new(), manifest.Permissions);
        using PluginBroker broker = new(scope, new FakeEnvironment(),
            new PluginDiagnosticsLog(Path.Combine(_root, "diag.log"), manifest.Id));
        PluginScopeException error = await Assert.ThrowsAsync<PluginScopeException>(() => broker.ExecuteAsync(PluginOps.HelperStart,
            JsonSerializer.SerializeToElement(new HelperStartRequest
            { HelperId = "device", TaskId = "task-no-consent", Payload = "{}", TimeoutMs = 1000 }, DtoJson.Options), default));
        Assert.Equal(PluginErrorCode.PermissionDenied, error.Code);
        Assert.Contains("high-trust", error.Message);
        Assert.Empty(Directory.GetDirectories(Path.Combine(_root, "scratch", "HelperTasks")));
    }

    [Fact]
    public async Task ApprovalIsRememberedAcrossRestartsForTheExactPackageDigest()
    {
        ConsentEnvironment environment = new() { Approve = true };
        await using (PluginSystemService service = Service(environment))
        {
            Assert.True(await service.AuthorizeNativeHelpersAsync(Manifest(), "digest-one"));
            Assert.True(await service.AuthorizeNativeHelpersAsync(Manifest(), "digest-one"));
            Assert.Single(environment.Requests);
            // A different package digest asks again.
            Assert.True(await service.AuthorizeNativeHelpersAsync(Manifest(), "digest-two"));
            Assert.Equal(2, environment.Requests.Count);
            Assert.Equal("digest-two", environment.Requests[1].PackageDigest);
            Assert.Equal(new[] { "helpers/device.exe" }, environment.Requests[1].HelperEntryPoints);
        }
        // Same digest after a restart: remembered via the shared registry, no prompt.
        await using PluginSystemService restarted = Service(environment);
        Assert.True(await restarted.AuthorizeNativeHelpersAsync(Manifest(), "digest-two"));
        Assert.Equal(2, environment.Requests.Count);
    }

    [Fact]
    public async Task DenialAndCancellationDoNotBecomeApproval()
    {
        ConsentEnvironment environment = new();
        await using PluginSystemService service = Service(environment);
        Assert.False(await service.AuthorizeNativeHelpersAsync(Manifest(), "digest"));
        environment.Approve = true;
        Assert.True(await service.AuthorizeNativeHelpersAsync(Manifest(), "digest"));
        Assert.Equal(2, environment.Requests.Count);
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.AuthorizeNativeHelpersAsync(Manifest(), "digest", cancelled.Token));
        Assert.Equal(2, environment.Requests.Count);
    }

    [Fact]
    public async Task ApprovalArrivingAfterCancellationIsNotCached()
    {
        using CancellationTokenSource cancellation = new();
        ConsentEnvironment environment = new()
        {
            Approve = true,
            OnRequest = () => cancellation.Cancel(),
        };
        await using PluginSystemService service = Service(environment);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.AuthorizeNativeHelpersAsync(Manifest(), "digest", cancellation.Token));
        environment.OnRequest = null;
        environment.Approve = false;
        Assert.False(await service.AuthorizeNativeHelpersAsync(Manifest(), "digest"));
        Assert.Equal(2, environment.Requests.Count);
    }

    private string? _sharedRoot;
    private PluginSystemService Service(IPluginHostEnvironment environment)
    {
        _sharedRoot ??= Path.Combine(_root, "shared-registry");
        Directory.CreateDirectory(_sharedRoot);
        string host = Path.Combine(_root, "never-launched-host.exe");
        File.WriteAllText(host, "Consent fixture; this executable is never launched.");
        PluginSystemService service = new(new PluginHostPaths(_sharedRoot), environment, host);
        // The real authorization flow always runs after the plugin's registry entry
        // exists; seed one only when absent so a persisted digest survives restarts.
        service.Registry.Load();
        service.Registry.Mutate(data =>
        {
            if (!data.Plugins.ContainsKey(Manifest().Id))
            {
                data.Plugins[Manifest().Id] = new PluginRegistryEntry
                {
                    Id = Manifest().Id,
                    ApprovedPermissions = [Permission.NativeHelpersExecute],
                };
            }
            return data;
        });
        return service;
    }

    private sealed class ConsentEnvironment : FakeEnvironment, IPluginHostEnvironment
    {
        public bool Approve { get; set; }
        public Action? OnRequest { get; set; }
        public List<HostNativeHelperConsent> Requests { get; } = [];
        public Task<bool> ConfirmNativeHelperExecutionAsync(HostNativeHelperConsent request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            OnRequest?.Invoke();
            return Task.FromResult(Approve);
        }
    }

    private static PluginManifest Manifest() => new()
    {
        Id = "org.example.device", Name = "Device helper", Version = "1.0.0",
        Permissions = [Permission.NativeHelpersExecute],
        NativeHelpers = [new PluginNativeHelper
        { Id = "device", EntryPoint = "helpers/device.exe", Rid = "win-x86", ProtocolVersion = "1.0" }],
    };

    private static HostNativeHelperConsent Request() => new()
    {
        PluginId = "org.example.device", PluginName = "Device helper", Version = "1.0.0",
        PackageDigest = "exact-package-digest", HelperEntryPoints = ["helpers/device.exe"],
    };

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
