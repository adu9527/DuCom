using System.Text.Json;
using DuCom.Plugin;
using DuCom.PluginHost.Core;
using DuCom.PluginHost.Packages;
using DuCom.PluginHost.Registry;
using Xunit;
using Registry = DuCom.PluginHost.Registry;

namespace DuCom.Plugins.Tests;

public sealed class ManifestValidationTests
{
    private static string BaseManifest => """
        {
          "manifestVersion": 1,
          "id": "org.example.test",
          "name": "Test",
          "version": "1.0.0",
          "protocolVersion": "1.0",
          "minHostVersion": "0.0.1",
          "entryAssembly": "Test.dll",
          "entryType": "Test.Plugin",
          "runtime": { "framework": "net10.0", "rid": "win-x64" },
          "capabilities": ["menu"],
          "permissions": []
        }
        """;

    [Fact]
    public void AcceptsValidManifest()
    {
        Assert.True(PluginManifestValidator.TryParseStrict(BaseManifest, out PluginManifest? manifest, out string? error));
        Assert.Null(error);
        Assert.True(PluginManifestValidator.Validate(manifest!, _ => false, out IReadOnlyList<string> errors));
        Assert.Empty(errors);
    }

    [Fact]
    public void RejectsDuplicateJsonProperties()
    {
        string json = BaseManifest.Replace("\"name\": \"Test\",", "\"name\": \"Test\", \"name\": \"Other\",");
        Assert.False(PluginManifestValidator.TryParseStrict(json, out PluginManifest? _, out string? error));
        Assert.NotNull(error);
        Assert.Contains("Duplicate", error, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsUnknownPermission()
    {
        string json = BaseManifest.Replace("\"permissions\": []", "\"permissions\": [\"network.full\"]");
        Assert.True(PluginManifestValidator.TryParseStrict(json, out PluginManifest? manifest, out _));
        Assert.False(PluginManifestValidator.Validate(manifest!, _ => false, out IReadOnlyList<string> errors));
        Assert.Contains(errors, error => error.Contains("Unknown permission", StringComparison.Ordinal));
    }

    [Fact]
    public void AcceptsRegisteredX86NativeHelper()
    {
        string json = BaseManifest.Replace(
            "\"permissions\": []",
            "\"permissions\": [\"native-helpers.execute\"], \"nativeHelpers\": [{\"id\":\"runner\",\"entryPoint\":\"helpers/win-x86/runner.exe\",\"rid\":\"win-x86\",\"protocolVersion\":\"1.0\",\"dependencies\":[\"helpers/win-x86/vendor.dll\"]}]");
        Assert.True(PluginManifestValidator.TryParseStrict(json, out PluginManifest? manifest, out string? parseError), parseError);
        Assert.True(PluginManifestValidator.Validate(manifest!, _ => false, out IReadOnlyList<string> errors), string.Join(Environment.NewLine, errors));
    }

    [Theory]
    [InlineData("../runner.exe")]
    [InlineData("C:/runner.exe")]
    [InlineData("helpers//runner.exe")]
    public void RejectsUnsafeNativeHelperEntryPoint(string entryPoint)
    {
        string json = BaseManifest.Replace(
            "\"permissions\": []",
            $"\"permissions\": [\"native-helpers.execute\"], \"nativeHelpers\": [{{\"id\":\"runner\",\"entryPoint\":\"{entryPoint}\",\"rid\":\"win-x86\",\"protocolVersion\":\"1.0\"}}]");
        Assert.True(PluginManifestValidator.TryParseStrict(json, out PluginManifest? manifest, out _));
        Assert.False(PluginManifestValidator.Validate(manifest!, _ => false, out IReadOnlyList<string> errors));
        Assert.Contains(errors, error => error.Contains("entryPoint", StringComparison.Ordinal));
    }

    [Fact]
    public void NativeHelperRequiresExecutionPermission()
    {
        string json = BaseManifest.Replace(
            "\"permissions\": []",
            "\"permissions\": [], \"nativeHelpers\": [{\"id\":\"runner\",\"entryPoint\":\"runner.exe\",\"rid\":\"win-x86\",\"protocolVersion\":\"1.0\"}]");
        Assert.True(PluginManifestValidator.TryParseStrict(json, out PluginManifest? manifest, out _));
        Assert.False(PluginManifestValidator.Validate(manifest!, _ => false, out IReadOnlyList<string> errors));
        Assert.Contains(errors, error => error.Contains(Permission.NativeHelpersExecute, StringComparison.Ordinal));
    }

    [Fact]
    public void RejectsUnsupportedNativeHelperProtocol()
    {
        string json = BaseManifest.Replace(
            "\"permissions\": []",
            "\"permissions\": [\"native-helpers.execute\"], \"nativeHelpers\": [{\"id\":\"runner\",\"entryPoint\":\"runner.exe\",\"rid\":\"win-x86\",\"protocolVersion\":\"2.0\"}]");
        Assert.True(PluginManifestValidator.TryParseStrict(json, out PluginManifest? manifest, out _));
        Assert.False(PluginManifestValidator.Validate(manifest!, _ => false, out IReadOnlyList<string> errors));
        Assert.Contains(errors, error => error.Contains("not supported", StringComparison.Ordinal));
    }

    [Fact]
    public void RejectsUnknownCapability()
    {
        string json = BaseManifest.Replace("\"capabilities\": [\"menu\"]", "\"capabilities\": [\"serial-send\"]");
        Assert.True(PluginManifestValidator.TryParseStrict(json, out PluginManifest? manifest, out _));
        Assert.False(PluginManifestValidator.Validate(manifest!, _ => false, out IReadOnlyList<string> errors));
        Assert.Contains(errors, error => error.Contains("Unknown capability", StringComparison.Ordinal));
    }

    [Fact]
    public void RejectsEntryAssemblyWithPathSeparators()
    {
        string json = BaseManifest.Replace("\"entryAssembly\": \"Test.dll\"", "\"entryAssembly\": \"..\\\\evil.dll\"");
        Assert.True(PluginManifestValidator.TryParseStrict(json, out PluginManifest? manifest, out _));
        Assert.False(PluginManifestValidator.Validate(manifest!, _ => false, out IReadOnlyList<string> errors));
        Assert.Contains(errors, error => error.Contains("entryAssembly", StringComparison.Ordinal));
    }

    [Fact]
    public void RejectsReservedNamespaceForThirdParties()
    {
        string json = BaseManifest.Replace("org.example.test", "com.ducom.evil");
        Assert.True(PluginManifestValidator.TryParseStrict(json, out PluginManifest? manifest, out _));
        Assert.False(PluginManifestValidator.Validate(manifest!, id => false, out IReadOnlyList<string> errors));
        Assert.Contains(errors, error => error.Contains("reserved", StringComparison.Ordinal));
    }

    [Fact]
    public void RejectsInvalidIdFormat()
    {
        string json = BaseManifest.Replace("org.example.test", "Not.A.Valid_ID");
        Assert.True(PluginManifestValidator.TryParseStrict(json, out PluginManifest? manifest, out _));
        Assert.False(PluginManifestValidator.Validate(manifest!, _ => false, out IReadOnlyList<string> errors));
    }

    [Fact]
    public void RejectsInvalidSemVer()
    {
        string json = BaseManifest.Replace("\"version\": \"1.0.0\"", "\"version\": \"1.0\"");
        Assert.True(PluginManifestValidator.TryParseStrict(json, out PluginManifest? manifest, out _));
        Assert.False(PluginManifestValidator.Validate(manifest!, _ => false, out IReadOnlyList<string> errors));
    }

    [Fact]
    public void ProtocolVersionComparison()
    {
        Assert.Equal((1, 0), PluginManifestValidator.ParseProtocolVersion("1.0"));
        Assert.Null(PluginManifestValidator.ParseProtocolVersion("1"));
        Assert.Null(PluginManifestValidator.ParseProtocolVersion("v1.0"));
        Assert.True(PluginManifestValidator.TryCompareSemVer("1.2.0", "1.10.0", out int comparison));
        Assert.True(comparison < 0);
    }
}

public sealed class WireFramingTests
{
    [Fact]
    public void EncodesAndDecodesFrames()
    {
        PluginWireMessage message = PluginWireMessage.Notify("sid", "aid", "worker.hello", JsonDocument.Parse("{\"credential\":\"abc\"}").RootElement);
        byte[] frame = PluginWire.Encode(message);
        Assert.True(frame.Length > 4);
        using MemoryStream stream = new(frame);
        PluginWireMessage? decoded = PluginWire.ReadAsync(stream, CancellationToken.None).ConfigureAwait(false).GetAwaiter().GetResult();
        Assert.NotNull(decoded);
        Assert.Equal("worker.hello", decoded!.Operation);
        Assert.Equal("sid", decoded.SessionId);
    }

    [Fact]
    public async Task RejectsOversizedFrameDeclaration()
    {
        byte[] header = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)(PluginWire.MaximumFrameBytes + 1));
        using MemoryStream stream = new(header);
        await Assert.ThrowsAsync<InvalidOperationException>(() => PluginWire.ReadAsync(stream, CancellationToken.None));
    }
}

public sealed class RegistryStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"ducom-registry-tests-{Guid.NewGuid():N}");
    private string _path => Path.Combine(_directory, "plugin-registry.json");

    [Fact]
    public void FaultDisablementSurvivesReload()
    {
        PluginRegistryStore store = new(_path);
        store.Load();
        store.Mutate(data =>
        {
            data.Plugins["org.example.x"] = new PluginRegistryEntry
            {
                Id = "org.example.x",
                FaultDisabled = new FaultDisableRecord { Version = "1.0.0", Reason = "test", ActivationId = "a1" },
            };
            return data;
        });

        PluginRegistryStore reloaded = new(_path);
        reloaded.Load();
        Assert.NotNull(reloaded.Current.Plugins["org.example.x"].FaultDisabled);
        Assert.Equal("test", reloaded.Current.Plugins["org.example.x"].FaultDisabled!.Reason);
    }

    [Fact]
    public void CorruptFileIsQuarantinedAndRestarted()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, "{ not json !!!");
        PluginRegistryStore store = new(_path);
        store.Load();
        string[] files = Directory.EnumerateFiles(Path.GetDirectoryName(_path)!).Select(Path.GetFileName).ToArray();
        Assert.True(store.Current.Plugins.Count == 0, $"plugins={store.Current.Plugins.Count}; exists={File.Exists(_path)}; files=[{string.Join(",", files)}]");
        Assert.Contains(files, file => file.StartsWith("plugin-registry.json.corrupt-", StringComparison.Ordinal));
    }

    [Fact]
    public void SafeStartFlagPersists()
    {
        PluginRegistryStore store = new(_path);
        store.Load();
        store.Mutate(data => data with { SafeStartAllPlugins = true });
        PluginRegistryStore reloaded = new(_path);
        reloaded.Load();
        Assert.True(reloaded.Current.SafeStartAllPlugins);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (Exception)
        {
        }
    }
}

public sealed class BudgetGovernorTests
{
    [Fact]
    public void RejectsWarningAboveTotal()
    {
        Assert.Throws<ArgumentException>(() => new BudgetGovernor(new BudgetGovernorConfig { TotalBudgetBytes = 1_000, WarningThresholdBytes = 2_000 }));
    }

    [Fact]
    public void EmergencyStopPrefersHighestWorker()
    {
        BudgetGovernor governor = new(new BudgetGovernorConfig
        {
            TotalBudgetBytes = 300_000_000,
            WarningThresholdBytes = 240_000_000,
            SampleIntervalMilliseconds = 3600_000,
        });
        BudgetStopRequest? request = null;
        governor.EmergencyStop += r => request = r;
        governor.Start();
        long hostBytes = governor.Sample().HostPrivateBytes;
        Assert.True(hostBytes > 0);
        Assert.Null(request);
        governor.Dispose();
    }
}
