using System.IO;
using System.Security.Cryptography;

namespace DuCom.Core.Updates;

/// <summary>
/// Sanity-checks a downloaded portable package before it is allowed to become the staged
/// update: non-empty size, Windows executable (MZ) header, and — when the release asset
/// publishes one — a SHA-256 digest match.
/// </summary>
public static class PortablePackageVerifier
{
    private const string Sha256Prefix = "sha256:";

    public static void Verify(string path, GitHubReleaseAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentException.ThrowIfNullOrEmpty(path);

        FileInfo file = new(path);
        if (!file.Exists)
        {
            throw new IOException($"Downloaded package '{path}' does not exist.");
        }

        if (file.Length == 0)
        {
            throw new IOException($"Downloaded package '{asset.Name}' is empty.");
        }

        if (asset.Size > 0 && file.Length != asset.Size)
        {
            throw new IOException($"Downloaded package '{asset.Name}' is {file.Length} bytes but the release asset reports {asset.Size} bytes.");
        }

        if (!HasPortableExecutableHeader(path))
        {
            throw new IOException($"Downloaded package '{asset.Name}' does not have a Windows executable (MZ) header.");
        }

        if (!MatchesSha256Digest(path, asset.Digest))
        {
            throw new IOException($"SHA-256 digest of '{asset.Name}' does not match the release digest '{asset.Digest}'.");
        }
    }

    public static bool HasPortableExecutableHeader(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        using FileStream stream = File.OpenRead(path);
        return stream.ReadByte() == 'M' && stream.ReadByte() == 'Z';
    }

    /// <summary>
    /// Returns true when the file matches the published digest. An absent or unsupported
    /// digest algorithm also returns true: HTTPS still protects the transfer, and an
    /// unknown digest format must not brick updates.
    /// </summary>
    public static bool MatchesSha256Digest(string path, string? digest)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        string? expected = TryParseSha256Hex(digest);
        if (expected is null)
        {
            return true;
        }

        using FileStream stream = File.OpenRead(path);
        string actual = Convert.ToHexString(SHA256.HashData(stream));
        return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryParseSha256Hex(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest))
        {
            return null;
        }

        string value = digest.Trim();
        if (!value.StartsWith(Sha256Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string hex = value[Sha256Prefix.Length..];
        return hex.Length == 64 && hex.All(IsHexDigit) ? hex : null;
    }

    private static bool IsHexDigit(char character) => character is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F');
}
