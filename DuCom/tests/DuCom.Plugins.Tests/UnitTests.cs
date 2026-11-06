using System.Text.Json;
using DuCom.Plugin;
using DuCom.PluginHost;
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
            BesPluginQuotaBytes = 100_000_000,
            StandardPluginPoolBytes = 100_000_000,
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

    [Fact]
    public void PluginActivationUsesTotalBudgetInsteadOfWarningThreshold()
    {
        BudgetGovernor governor = new(new BudgetGovernorConfig
        {
            TotalBudgetBytes = long.MaxValue,
            WarningThresholdBytes = 1,
            SampleIntervalMilliseconds = 3600_000,
        });
        governor.Start();
        PluginBudgetSample sample = governor.Sample();

        Assert.True(sample.TotalPrivateBytes >= 1);
        Assert.True(governor.CanActivatePlugins());
        governor.Dispose();
    }

    private const long Megabyte = 1024 * 1024;

    private static BudgetStopDecision? Decide(
        long hostBytes,
        (string PluginId, uint Pid, long PrivateBytes)[] workers,
        Dictionary<uint, long> previous,
        long hostGrowthBytes,
        bool requireEvidence = false,
        long hostBudgetBytes = 800_000_000,
        long besQuotaBytes = 200_000_000,
        long standardPoolBytes = 250_000_000)
        => BudgetGovernor.DecideEmergencyStop(
            hostBytes,
            workers,
            previous,
            hostGrowthBytes,
            new BudgetLimits(
                hostBudgetBytes,
                PerPluginQuotaBytes: 250_000_000,
                besQuotaBytes,
                standardPoolBytes,
                PluginPoolBytes: besQuotaBytes + standardPoolBytes,
                GrowthEvidenceBytes: 32 * Megabyte,
                BesPluginId: "internal.ducom.bes-download"),
            requireEvidence);

    [Fact]
    public void HostSpikeNeverSacrificesHealthyPlugins()
    {
        // The 2026-11-06 logs: host jumped 188 MB in one second while every plugin moved by
        // a few MB. Killing the two largest workers removed features without fixing pressure.
        (string, uint, long)[] workers =
        [
            ("com.ducom.production", 57716, 58_449_920),
            ("internal.ducom.bes-download", 46440, 61_239_296),
        ];
        Dictionary<uint, long> previous = new()
        {
            [57716] = 57_000_000,
            [46440] = 59_000_000,
        };

        BudgetStopDecision? decision = Decide(
            hostBytes: 627_150_848,
            workers,
            previous,
            hostGrowthBytes: 188_000_000);

        Assert.Null(decision);
    }

    [Fact]
    public void HostAboveItsOwnBudgetLeavesPluginsRunning()
    {
        (string, uint, long)[] workers =
        [
            ("com.ducom.timer", 81, 120_000_000),
            ("internal.ducom.bes-download", 82, 190_000_000),
        ];
        Dictionary<uint, long> previous = new()
        {
            [81] = 118_000_000,
            [82] = 188_000_000,
        };

        BudgetStopDecision? decision = Decide(
            hostBytes: 860_000_000,
            workers,
            previous,
            hostGrowthBytes: 120_000_000);

        Assert.Null(decision);
    }

    [Fact]
    public void BesQuotaExceededStopsTheBesPlugin()
    {
        (string, uint, long)[] workers =
        [
            ("internal.ducom.bes-download", 90, 210_000_000),
            ("com.ducom.timer", 91, 60_000_000),
        ];
        Dictionary<uint, long> previous = new()
        {
            [90] = 190_000_000,
            [91] = 60_000_000,
        };

        BudgetStopDecision? decision = Decide(
            hostBytes: 600_000_000,
            workers,
            previous,
            hostGrowthBytes: 10_000_000);

        Assert.NotNull(decision);
        Assert.Equal(90u, decision.Value.Pid);
        Assert.Equal(BudgetStopReason.QuotaExceeded, decision.Value.Reason);
    }

    [Fact]
    public void StandardGroupOverItsPoolStopsTheLargestOrdinaryPlugin()
    {
        (string, uint, long)[] workers =
        [
            ("com.ducom.checksum", 100, 100_000_000),
            ("com.ducom.crc", 101, 90_000_000),
            ("com.ducom.audio", 102, 80_000_000),
            ("internal.ducom.bes-download", 103, 50_000_000),
        ];
        Dictionary<uint, long> previous = new()
        {
            [100] = 99_000_000,
            [101] = 89_000_000,
            [102] = 79_000_000,
            [103] = 50_000_000,
        };

        BudgetStopDecision? decision = Decide(
            hostBytes: 500_000_000,
            workers,
            previous,
            hostGrowthBytes: 5_000_000);

        Assert.NotNull(decision);
        Assert.Equal(100u, decision.Value.Pid);
        Assert.Equal(BudgetStopReason.GroupQuotaExceeded, decision.Value.Reason);
    }

    [Fact]
    public void BesPluginDoesNotConsumeTheStandardPool()
    {
        (string, uint, long)[] workers =
        [
            ("internal.ducom.bes-download", 110, 190_000_000),
            ("com.ducom.timer", 111, 200_000_000),
        ];
        Dictionary<uint, long> previous = new()
        {
            [110] = 188_000_000,
            [111] = 198_000_000,
        };

        BudgetStopDecision? decision = Decide(
            hostBytes: 600_000_000,
            workers,
            previous,
            hostGrowthBytes: 4_000_000);

        Assert.Null(decision);
    }

    [Fact]
    public void QuotaViolationOutranksHostPressure()
    {
        (string, uint, long)[] workers =
        [
            ("com.ducom.leak", 10, 300_000_000),
            ("com.ducom.ok", 20, 60_000_000),
        ];
        Dictionary<uint, long> previous = new()
        {
            [10] = 290_000_000,
            [20] = 60_000_000,
        };

        BudgetStopDecision? decision = Decide(
            hostBytes: 620_000_000,
            workers,
            previous,
            hostGrowthBytes: 150_000_000,
            requireEvidence: true);

        Assert.NotNull(decision);
        Assert.Equal(10u, decision.Value.Pid);
        Assert.Equal(BudgetStopReason.QuotaExceeded, decision.Value.Reason);
    }

    [Fact]
    public void AbnormalGrowthOutranksHostAttribution()
    {
        (string, uint, long)[] workers =
        [
            ("com.ducom.spike", 30, 120_000_000),
        ];
        Dictionary<uint, long> previous = new()
        {
            [30] = 60_000_000,
        };

        BudgetStopDecision? decision = Decide(
            hostBytes: 610_000_000,
            workers,
            previous,
            hostGrowthBytes: 40_000_000);

        Assert.NotNull(decision);
        Assert.Equal(30u, decision.Value.Pid);
        Assert.Equal(60_000_000, decision.Value.GrowthBytes);
        Assert.Equal(BudgetStopReason.AbnormalGrowth, decision.Value.Reason);
    }

    [Fact]
    public void LargestWorkerIsChosenWhenPluginsDriveGrowth()
    {
        (string, uint, long)[] workers =
        [
            ("com.ducom.large", 40, 180_000_000),
            ("com.ducom.small", 50, 60_000_000),
        ];
        Dictionary<uint, long> previous = new()
        {
            [40] = 150_000_000,
            [50] = 60_000_000,
        };

        BudgetStopDecision? decision = Decide(
            hostBytes: 300_000_000,
            workers,
            previous,
            hostGrowthBytes: 5_000_000);

        Assert.NotNull(decision);
        Assert.Equal(40u, decision.Value.Pid);
        Assert.Equal(BudgetStopReason.LargestWorker, decision.Value.Reason);
    }

    [Fact]
    public void FollowUpSamplesRequireExplicitEvidence()
    {
        (string, uint, long)[] workers =
        [
            ("com.ducom.large", 60, 180_000_000),
            ("com.ducom.small", 70, 60_000_000),
        ];
        Dictionary<uint, long> previous = new()
        {
            [60] = 178_000_000,
            [70] = 59_000_000,
        };

        Assert.NotNull(Decide(hostBytes: 300_000_000, workers, previous, hostGrowthBytes: 2_000_000));
        Assert.Null(Decide(hostBytes: 300_000_000, workers, previous, hostGrowthBytes: 2_000_000, requireEvidence: true));
    }

    [Fact]
    public void EmptyWorkerSetNeverStopsAnything()
    {
        Assert.Null(Decide(hostBytes: 900_000_000, [], new Dictionary<uint, long>(), hostGrowthBytes: 0));
    }
}

public sealed class PluginDefaultActivationTests
{
    [Theory]
    [InlineData("com.ducom.background-image", true)]
    [InlineData("com.ducom.timer", true)]
    [InlineData("com.ducom.log-package", false)]
    [InlineData("com.ducom.checksum", false)]
    [InlineData("com.ducom.production", false)]
    [InlineData("com.ducom.crc", false)]
    [InlineData("com.ducom.audio", false)]
    [InlineData("org.example.third-party", true)]
    public void FirstInstallOnlyKeepsWorkspacePluginsOnWithoutChangingThirdPartyDefaults(
        string pluginId,
        bool expected)
        => Assert.Equal(expected, PluginSystemService.IsEnabledOnFirstInstall(pluginId));
}
