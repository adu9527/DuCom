using System.Text.Json;
using DuCom.Plugin;
using DuCom.Plugin.Dto;
using DuCom.PluginHost.Core;
using DuCom.PluginHost.Diagnostics;
using Xunit;

namespace DuCom.Plugins.Tests;

public sealed class ProductionBrokerTests
{
    [Fact]
    public async Task LeasedIoRejectsForeignLeaseAndMissingPermissionBeforeTouchingDevice()
    {
        string root = Path.Combine(Path.GetTempPath(), "ducom-serial-io-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (string[] permissions in new[] { Array.Empty<string>(), new[] { Permission.SerialLease } })
            {
                PluginManifest manifest = new() { Id = "org.example.serial-io", Permissions = permissions };
                using ActivationScope scope = new(manifest, Guid.NewGuid().ToString("N"), Path.Combine(root, "storage"), Path.Combine(root, "temp"), Path.Combine(root, "output"), Path.Combine(root, "snapshots"), new(), permissions);
                using PluginBroker broker = new(scope, new FakeEnvironment(), new PluginDiagnosticsLog(Path.Combine(root, "diag.log"), manifest.Id));
                foreach (string operation in new[] { PluginOps.SerialLeaseOpen, PluginOps.SerialLeaseWrite, PluginOps.SerialLeaseRead })
                {
                    PluginScopeException error = await Assert.ThrowsAsync<PluginScopeException>(() => broker.ExecuteAsync(operation,
                        JsonSerializer.SerializeToElement(new SerialLeaseIoRequest { LeaseId = "another-activation", Bytes = [0x55] }, DtoJson.Options), default));
                    Assert.Equal(PluginErrorCode.PermissionDenied, error.Code);
                }
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
