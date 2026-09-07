using System.Security.Cryptography;
using System.Text;
using DuCom.Core.Updates;

namespace DuCom.Core.Tests.Updates;

public sealed class PortablePackageVerifierTests : IDisposable
{
    private readonly string _directory;

    public PortablePackageVerifierTests() => _directory = Directory.CreateTempSubdirectory("ducom-verifier-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string WritePackage(string content)
    {
        string path = Path.Combine(_directory, Path.GetRandomFileName());
        File.WriteAllText(path, content, Encoding.ASCII);
        return path;
    }

    private static GitHubReleaseAsset Asset(long size, string? digest = null) =>
        new("DuCom.exe", "https://example.com/DuCom.exe", size, Digest: digest);

    [Fact]
    public void ExecutableHeaderIsDetected()
    {
        string path = WritePackage("MZpayload");

        Assert.True(PortablePackageVerifier.HasPortableExecutableHeader(path));
    }

    [Fact]
    public void NonExecutableHeaderIsRejected()
    {
        string path = WritePackage("HTML document");

        Assert.False(PortablePackageVerifier.HasPortableExecutableHeader(path));
        Assert.Throws<IOException>(() => PortablePackageVerifier.Verify(path, Asset(13)));
    }

    [Fact]
    public void EmptyFileIsRejected()
    {
        string path = WritePackage(string.Empty);

        Assert.Throws<IOException>(() => PortablePackageVerifier.Verify(path, Asset(0)));
    }

    [Fact]
    public void SizeMismatchIsRejected()
    {
        string path = WritePackage("MZ123456");

        Assert.Throws<IOException>(() => PortablePackageVerifier.Verify(path, Asset(999)));
    }

    [Fact]
    public void MatchingSha256DigestPasses()
    {
        byte[] payload = Encoding.ASCII.GetBytes("MZ-match");
        string path = WritePackage(Encoding.ASCII.GetString(payload));
        string digest = $"sha256:{Convert.ToHexString(SHA256.HashData(payload))}";

        PortablePackageVerifier.Verify(path, Asset(payload.Length, digest));
    }

    [Fact]
    public void MismatchingSha256DigestThrows()
    {
        string path = WritePackage("MZ-mismatch");
        byte[] other = Encoding.ASCII.GetBytes("other payload");
        string digest = $"sha256:{Convert.ToHexString(SHA256.HashData(other))}";

        Assert.Throws<IOException>(() => PortablePackageVerifier.Verify(path, Asset(11, digest)));
        Assert.False(PortablePackageVerifier.MatchesSha256Digest(path, digest));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sha512:abcdef")]
    [InlineData("sha256:tooshort")]
    public void AbsentOrUnsupportedDigestsDoNotBlockUpdates(string? digest)
    {
        string path = WritePackage("MZ-no-digest");

        Assert.True(PortablePackageVerifier.MatchesSha256Digest(path, digest));
    }
}
