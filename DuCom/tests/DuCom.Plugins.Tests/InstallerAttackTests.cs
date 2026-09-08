using System.IO.Compression;
using System.Text;
using DuCom.PluginHost.Packages;
using PackageValidationResult = DuCom.PluginHost.Packages.PackageValidationResult;
using Xunit;

namespace DuCom.Plugins.Tests;

public sealed class InstallerAttackTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ducom-installer-{Guid.NewGuid():N}");

    private static void WriteBaseManifest(string directory)
    {
        Directory.CreateDirectory(directory);
        string manifest = """
            {
              "manifestVersion": 1,
              "id": "org.example.attack",
              "name": "Attack",
              "version": "1.0.0",
              "protocolVersion": "1.0",
              "minHostVersion": "0.0.1",
              "entryAssembly": "Lib.dll",
              "entryType": "Attack.Plugin",
              "runtime": { "framework": "net10.0", "rid": "win-x64" },
              "capabilities": ["menu"],
              "permissions": []
            }
            """;
        File.WriteAllText(Path.Combine(directory, "plugin.manifest.json"), manifest);
        File.WriteAllText(Path.Combine(directory, "Lib.dll"), "fake");
    }

    private string CreateZip(params (string Name, byte[] Content)[] entries)
    {
        string zipPath = Path.Combine(_root, $"pack-{Guid.NewGuid():N}.dcpack");
        Directory.CreateDirectory(_root);
        using FileStream stream = File.Create(zipPath);
        using ZipArchive archive = new(stream, ZipArchiveMode.Create);
        foreach ((string name, byte[] content) in entries)
        {
            ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Fastest);
            using MemoryStream buffer = new(content);
            using Stream target = entry.Open();
            buffer.CopyTo(target);
        }

        return zipPath;
    }

    [Fact]
    public void RejectsPathTraversalEntries()
    {
        WriteBaseManifest(Path.Combine(_root, "stage"));
        PluginPackageInstaller installer = new(Path.Combine(_root, "installed"));
        string zip = CreateZip(
            ("plugin.manifest.json", File.ReadAllBytes(Path.Combine(_root, "stage", "plugin.manifest.json"))),
            ("../evil.txt", "x"u8.ToArray()));
        Assert.Throws<PluginInstallException>(() => installer.InstallDcPack(zip, _ => false));
    }

    [Fact]
    public void RejectsAbsoluteAndDriveEntries()
    {
        WriteBaseManifest(Path.Combine(_root, "stage"));
        PluginPackageInstaller installer = new(Path.Combine(_root, "installed"));
        string zip = CreateZip(
            ("plugin.manifest.json", File.ReadAllBytes(Path.Combine(_root, "stage", "plugin.manifest.json"))),
            (@"C:/Windows/evil.txt", "x"u8.ToArray()));
        Assert.Throws<PluginInstallException>(() => installer.InstallDcPack(zip, _ => false));
    }

    [Fact]
    public void RejectsAlternateDataStreamStyleEntries()
    {
        WriteBaseManifest(Path.Combine(_root, "stage"));
        PluginPackageInstaller installer = new(Path.Combine(_root, "installed"));
        string zip = CreateZip(
            ("plugin.manifest.json", File.ReadAllBytes(Path.Combine(_root, "stage", "plugin.manifest.json"))),
            ("file.txt:hiddenstream", "x"u8.ToArray()));
        Assert.Throws<PluginInstallException>(() => installer.InstallDcPack(zip, _ => false));
    }

    [Fact]
    public void RejectsCaseCollidingDuplicates()
    {
        WriteBaseManifest(Path.Combine(_root, "stage"));
        PluginPackageInstaller installer = new(Path.Combine(_root, "installed"));
        string zip = CreateZip(
            ("plugin.manifest.json", File.ReadAllBytes(Path.Combine(_root, "stage", "plugin.manifest.json"))),
            ("Lib.dll", "a"u8.ToArray()),
            ("lib.DLL", "b"u8.ToArray()));
        Assert.Throws<PluginInstallException>(() => installer.InstallDcPack(zip, _ => false));
    }

    [Fact]
    public void RejectsDeclaredUncompressedBomb()
    {
        WriteBaseManifest(Path.Combine(_root, "stage"));
        PluginPackageInstaller installer = new(Path.Combine(_root, "installed"));
        string zip = CreateZip(
            ("plugin.manifest.json", File.ReadAllBytes(Path.Combine(_root, "stage", "plugin.manifest.json"))),
            ("big.bin", new byte[PluginPackageInstaller.MaximumTotalUncompressedBytes]));
        Assert.Throws<PluginInstallException>(() => installer.InstallDcPack(zip, _ => false));
    }

    [Fact]
    public void RejectsZipSymlinkEntries()
    {
        string directory = Path.Combine(_root, $"manifest-{Guid.NewGuid():N}");
        WriteBaseManifest(directory);
        PluginPackageInstaller installer = new(Path.Combine(_root, "installed"));
        string zipPath = Path.Combine(_root, $"symlink-{Guid.NewGuid():N}.dcpack");
        Directory.CreateDirectory(_root);
        using (FileStream stream = File.Create(zipPath))
        using (ZipArchive archive = new(stream, ZipArchiveMode.Create))
        {
            ZipArchiveEntry manifest = archive.CreateEntry("plugin.manifest.json");
            using (Stream target = manifest.Open())
            using (MemoryStream source = new(File.ReadAllBytes(Path.Combine(directory, "plugin.manifest.json"))))
            {
                source.CopyTo(target);
            }

            ZipArchiveEntry link = archive.CreateEntry("link");
            link.ExternalAttributes = unchecked((int)0xA1FF0000);
            using (Stream target = link.Open())
            using (MemoryStream source = new("C:/evil"u8.ToArray()))
            {
                source.CopyTo(target);
            }
        }

        Assert.Throws<PluginInstallException>(() => installer.InstallDcPack(zipPath, _ => false));
    }

    [Fact]
    public void InstallsValidPackageIntoVersionDirectory()
    {
        string stage = Path.Combine(_root, $"ok-{Guid.NewGuid():N}");
        WriteBaseManifest(stage);
        PluginPackageInstaller installer = new(Path.Combine(_root, "installed"));
        string zip = CreateZip(
            ("plugin.manifest.json", File.ReadAllBytes(Path.Combine(stage, "plugin.manifest.json"))),
            ("Lib.dll", "managed"u8.ToArray()));
        PackageValidationResult result = installer.InstallDcPack(zip, _ => false);
        Assert.True(result.Accepted, string.Join("; ", result.Errors));
        Assert.True(Directory.Exists(installer.GetVersionDirectory("org.example.attack", "1.0.0")));
        Assert.NotEqual(string.Empty, result.Digest);
    }

    [Fact]
    public void ReinstallingIdenticalContentIsIdempotent()
    {
        string stage = Path.Combine(_root, $"idem-{Guid.NewGuid():N}");
        WriteBaseManifest(stage);
        PluginPackageInstaller installer = new(Path.Combine(_root, "installed"));
        byte[] manifest = File.ReadAllBytes(Path.Combine(stage, "plugin.manifest.json"));
        string zip1 = CreateZip(("plugin.manifest.json", manifest), ("Lib.dll", "a"u8.ToArray()));
        PackageValidationResult first = installer.InstallDcPack(zip1, _ => false);
        Assert.True(first.Accepted);
        string zip2 = CreateZip(("plugin.manifest.json", manifest), ("Lib.dll", "a"u8.ToArray()));
        PackageValidationResult second = installer.InstallDcPack(zip2, _ => false);
        Assert.True(second.Accepted);
    }

    [Fact]
    public void DirectoryDigestDoesNotAliasDelimitedPathAndContent()
    {
        string first = Path.Combine(_root, "digest-first");
        string second = Path.Combine(_root, "digest-second");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        // The former delimiter scheme encoded both trees as "a:b;c:d;".
        File.WriteAllText(Path.Combine(first, "a"), "b;c:d");
        File.WriteAllText(Path.Combine(second, "a"), "b");
        File.WriteAllText(Path.Combine(second, "c"), "d");

        Assert.NotEqual(
            PluginPackageInstaller.ComputeDirectoryDigest(first),
            PluginPackageInstaller.ComputeDirectoryDigest(second));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception)
        {
        }
    }
}
