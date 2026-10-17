using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DuCom.Plugin;
using DuCom.PluginHost;
using DuCom.PluginHost.Core;
using DuCom.PluginHost.Packages;
using DuCom.PluginHost.Registry;
using DuCom.Plugins.Checksum;
using DuCom.Services.Plugins;
using Xunit;

namespace DuCom.Plugins.Tests;

[Collection("WorkerLifecycle")]
public sealed class ChecksumPluginTests
{
    [Fact]
    public void BesFullImageCrcShaAndStableCodeAreVerifiedAtNonzeroOffset()
    {
        byte[] image = CreateBesImage();
        FirmwareResult result = FirmwareAnalyzer.Analyze(image, "arbitrary.prd");
        Assert.Equal("恒玄", result.Manufacturer);
        Assert.Equal("烧录固件", result.FileType);
        Assert.True(result.CrcValid);
        Assert.True(result.ImageValid);
        Assert.Equal("测试耳机", result.BluetoothName);
        image[0] ^= 1;
        Assert.True(FirmwareAnalyzer.Analyze(image, "corrupt.bin").IsCorrupt);
    }

    [Fact]
    public void MinimalGuideFieldsDoNotClaimFullImageVerification()
    {
        string sha = new('A', 64);
        string code = FirmwareAnalyzer.Crc32(Convert.FromHexString(sha)).ToString("X8", CultureInfo.InvariantCulture);
        byte[] image = Encoding.UTF8.GetBytes($"\nCHIP=best1502p\nKERNEL=RTX5\nBUILD_DATE=Oct 17 2026 12:00:00\nBUILD_SHA256={sha}\nFIRMWARE_CODE={code}\nBT_NAME=测试耳机   \n\0");
        FirmwareResult result = FirmwareAnalyzer.Analyze(image, "minimal.bin");
        Assert.Equal(code, result.FirmwareCode);
        Assert.Equal("测试耳机", result.BluetoothName);
        Assert.Null(result.ImageValid);
        Assert.Contains("未完成整文件", result.Status);
        Assert.Throws<InvalidDataException>(() => FirmwareAnalyzer.Analyze(Encoding.ASCII.GetBytes("\nCHIP=best1502p\nKERNEL=RTX5\nBUILD_DATE=x\nFIRMWARE_CODE=12345678\n\0"), "missing-sha.bin"));
    }

    [Fact]
    public void UnknownSuffixAndPrintableVendorStringsDoNotIdentifyVendor()
    {
        FirmwareResult result = FirmwareAnalyzer.Analyze("31D2-C47340F4 DB27CCE3_B0FAB3A8 AC700N PRD JL_FW"u8.ToArray(), "fake.prd");
        Assert.Equal("未知厂商", result.Manufacturer);
        Assert.Empty(result.FirmwareCode);
    }

    [Fact]
    public void BesLzmaOtaDecodesAndMatchesInternalCrc()
    {
        byte[] ota = Convert.FromBase64String("/////wAAAJhdAACAAP//////////AAURyNJhy3STZcSNsJwS/G8Vm4xztBcMrY+49rRu6gX1rIejYWF3U1pQwkOI9xk/j78zMJziQEfJHcsrlFLS0HJ1IVbEXhouzCB2TSar8eeDD1KGxgHVc6GiqTp+bEzknSzv+SPp0DpR8vw/590YTIKLsoaKHE3YYs7pyMeV75Vd1a2k1X//7x85YENSQzMyX09GX0lNQUdFPTB4QTI0QTUwRkIK");
        FirmwareResult result = FirmwareAnalyzer.Analyze(ota, "ota.prd");
        Assert.Equal("恒玄", result.Manufacturer);
        Assert.Equal("OTA升级固件", result.FileType);
        Assert.Equal("A2B10169", result.FirmwareCode);
        Assert.True(result.CrcValid);
        Assert.True(result.PackageCrcMatches);
        Assert.Null(result.ImageValid);
        ota[^2] ^= 1;
        Assert.True(FirmwareAnalyzer.Analyze(ota, "corrupt.bin").IsCorrupt);
    }

    [HostBuildFact]
    public async Task EmbeddedPackRunsInSandboxAndReadsSelectedFirmwareThroughBroker()
    {
        string root = Path.Combine(Path.GetTempPath(), "ducom-checksum-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string source = Path.Combine(root, "测试固件.prd");
            await File.WriteAllBytesAsync(source, CreateBesImage());
            SelectedFirmwareEnvironment environment = new(source);
            await using BedHarness harness = BedHarness.Create("checksum", environment: environment);
            PluginRuntimeController controller = Install(harness);
            Assert.True(await harness.Service.StartRegisteredAsync(controller.Manifest.Id));
            Assert.True(await controller.InvokeCommandAsync("select", new Dictionary<string, string>()));
            IReadOnlyList<UiNode> nodes = environment.Pages.Last();
            Assert.True(UiContributionValidator.Validate(nodes, out string? error), error);
            Assert.Contains(nodes.OfType<UiLabelNode>(), node => node.Text == "完整性校验通过");
            Assert.Contains(nodes.OfType<UiLabelNode>(), node => node.Text == "厂商：恒玄");
            Assert.Contains(nodes.OfType<UiTextNode>(), node => node.FieldId == "checksum" && node.Text.Length == 8);
            Assert.NotEqual(0u, controller.WorkerPid);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [HostBuildFact]
    public async Task RealVendorSamplesMatchOfficialCodesThroughSandbox()
    {
        string? samples = Environment.GetEnvironmentVariable("DUCOM_CHECKSUM_SAMPLES");
        if (string.IsNullOrEmpty(samples)) return; // Optional proprietary fixtures are never bundled.
        SelectedFirmwareEnvironment environment = new("");
        await using BedHarness harness = BedHarness.Create("checksum-real", environment: environment);
        PluginRuntimeController controller = Install(harness);
        Assert.True(await harness.Service.StartRegisteredAsync(controller.Manifest.Id));
        foreach (var sample in new[]
        {
            (Path: "杰理/jl_isd.fw", Code: "31D2-C47340F4", Name: "TOZO A1"),
            (Path: "杰理/update.ufw", Code: "31D2-C47340F4", Name: "TOZO A1"),
            (Path: "蓝讯/fw5000.prd", Code: "DB27CCE3_B0FAB3A8", Name: "HTS394-Du"),
            (Path: "蓝讯/fw5000.upd", Code: "DB27CCE3_B0FAB3A8", Name: "HTS394-Du"),
        })
        {
            environment.Source = Path.Combine(samples, sample.Path);
            Assert.True(await controller.InvokeCommandAsync("select", new Dictionary<string, string>()));
            IReadOnlyList<UiNode> page = environment.Pages.Last();
            Assert.Contains(page.OfType<UiTextNode>(), node => node.FieldId == "checksum" && node.Text == sample.Code);
            Assert.Contains(page.OfType<UiLabelNode>(), node => node.Text == "蓝牙名：" + sample.Name);
            Assert.Contains(page.OfType<UiLabelNode>(), node => node.Text.StartsWith("时间：", StringComparison.Ordinal) && node.Text.Contains("（文件修改时间）", StringComparison.Ordinal));
            byte[] damaged = await File.ReadAllBytesAsync(environment.Source);
            damaged[4] ^= 1;
            Assert.Throws<InvalidDataException>(() => FirmwareAnalyzer.Analyze(damaged, "damaged.bin"));
            Assert.Equal(sample.Code, FirmwareAnalyzer.Analyze(await File.ReadAllBytesAsync(environment.Source), "renamed.unknown").FirmwareCode);
        }
    }

    private static PluginRuntimeController Install(BedHarness harness)
    {
        FactoryPackDefinition pack = Assert.Single(PluginSystemHost.BuildFactoryPacks(), pack => pack.PluginId == "com.ducom.checksum");
        Assert.Contains(pack.Files, file => file.RelativePath == "SharpCompress.dll");
        string target = Path.Combine(harness.Paths.InstalledRoot, pack.PluginId, pack.Version);
        Directory.CreateDirectory(target);
        foreach (FactoryPackFile file in pack.Files) File.WriteAllBytes(Path.Combine(target, file.RelativePath), file.Content());
        harness.Service.Registry.Mutate(data => data.Plugins[pack.PluginId] = new PluginRegistryEntry
        {
            Id = pack.PluginId, SelectedVersion = pack.Version, Enabled = true,
            ApprovedPermissions = [Permission.FilesUserSelectedRead],
            InstalledVersions = [new InstalledVersionRecord { Version = pack.Version, Path = target, Digest = PluginPackageInstaller.ComputeDirectoryDigest(target) }],
        });
        return Assert.IsType<PluginRuntimeController>(harness.Service.GetOrCreateController(pack.PluginId));
    }

    internal static byte[] CreateBesImage()
    {
        string build = new('A', 64);
        string code = FirmwareAnalyzer.Crc32(Convert.FromHexString(build)).ToString("X8", CultureInfo.InvariantCulture);
        byte[] data = Encoding.UTF8.GetBytes("SYNTHETIC-TEST-PREFIX\nCHIP=best1502p\nKERNEL=RTX5\nBUILD_DATE=2026-10-17 11:36:10\n"
            + "FW_ID_SCHEMA=1\nSOURCE_SHA256=" + new string('B', 64) + "\nBUILD_SHA256=" + build + "\nFIRMWARE_CODE=" + code
            + "\nIDENTITY_TARGET=synthetic-test\nBT_NAME=测试耳机\nIMAGE_SHA256=" + new string('0', 64) + "\nCRC32_OF_IMAGE=0x00000000\n\0");
        int sha = data.AsSpan().IndexOf("IMAGE_SHA256="u8) + 13;
        int crc = data.AsSpan().IndexOf("CRC32_OF_IMAGE=0x"u8) + 17;
        Encoding.ASCII.GetBytes(Convert.ToHexString(SHA256.HashData(data))).CopyTo(data, sha);
        Encoding.ASCII.GetBytes(FirmwareAnalyzer.Crc32(data.AsSpan(0, crc)).ToString("X8", CultureInfo.InvariantCulture)).CopyTo(data, crc);
        return data;
    }

    private sealed class SelectedFirmwareEnvironment(string source) : FakeEnvironment
    {
        public string Source { get; set; } = source;
        public ConcurrentQueue<IReadOnlyList<UiNode>> Pages { get; } = new();
        public override Task<HostPickResult?> PickReadAsync(string pluginId, HostPickRequest request, CancellationToken cancellationToken) =>
            Task.FromResult<HostPickResult?>(new HostPickResult { DisplayPath = Source, IsDirectory = false, Remembered = false });
        public override void UpdateToolPage(string pluginId, string contributionId, IReadOnlyList<UiNode> nodes) => Pages.Enqueue(nodes);
    }
}
