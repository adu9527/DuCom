using System.IO.Compression;
using System.Security.Cryptography;
using DuCom.Plugin;

namespace DuCom.PluginHost.Packages;

public sealed record PackageValidationResult
{
    public required bool Accepted { get; init; }
    public required IReadOnlyList<string> Errors { get; init; }
    public required PluginManifest Manifest { get; init; }
    public required IReadOnlyList<PackageFileRecord> Files { get; init; }
    public required string Digest { get; init; }
}

public sealed record PackageFileRecord
{
    public required string RelativePath { get; init; }
    public required string FullPath { get; init; }
    public required long Length { get; init; }
}

public sealed class PluginInstallException : Exception
{
    public PluginInstallException(string message)
        : base(message)
    {
    }
}

public sealed class PluginPackageInstaller
{
    public const int MaximumFileCount = 2000;
    public const long MaximumSingleFileBytes = 128 * 1024 * 1024;
    public const long MaximumTotalUncompressedBytes = 384 * 1024 * 1024;
    public const int MaximumPathLength = 200;
    public const string ManifestEntryName = "plugin.manifest.json";

    private readonly string _pluginsRoot;

    public PluginPackageInstaller(string pluginsRoot)
    {
        ArgumentException.ThrowIfNullOrEmpty(pluginsRoot);
        _pluginsRoot = pluginsRoot;
        Directory.CreateDirectory(pluginsRoot);
    }

    public string GetVersionDirectory(string pluginId, string version) => Path.Combine(_pluginsRoot, pluginId, version);

    public string GetStorageDirectory(string pluginId) => Path.Combine(_pluginsRoot, "Data", pluginId);

    public string GetTempRoot(string pluginId) => Path.Combine(_pluginsRoot, "Temp", pluginId);

    public static void ValidateExtractedPackage(string directory, Func<string, bool> isOfficialNamespace, out PackageValidationResult result)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        List<string> errors = [];
        string manifestPath = Path.Combine(directory, ManifestEntryName);
        if (!File.Exists(manifestPath))
        {
            result = new PackageValidationResult
            {
                Accepted = false,
                Errors = [$"Package is missing '{ManifestEntryName}'."],
                Manifest = new PluginManifest(),
                Files = [],
                Digest = string.Empty,
            };
            return;
        }

        string json = File.ReadAllText(manifestPath);
        if (!PluginManifestValidator.TryParseStrict(json, out PluginManifest? manifest, out string? parseError))
        {
            result = new PackageValidationResult
            {
                Accepted = false,
                Errors = [$"Manifest rejected: {parseError}"],
                Manifest = manifest ?? new PluginManifest(),
                Files = [],
                Digest = string.Empty,
            };
            return;
        }

        if (!PluginManifestValidator.Validate(manifest!, isOfficialNamespace, out IReadOnlyList<string> validationErrors))
        {
            result = new PackageValidationResult
            {
                Accepted = false,
                Errors = [.. validationErrors.Select(error => $"Manifest rejected: {error}")],
                Manifest = manifest!,
                Files = [],
                Digest = string.Empty,
            };
            return;
        }

        string[] files = Directory.GetFiles(directory, "*", SearchOption.AllDirectories);
        if (files.Length > MaximumFileCount)
        {
            errors.Add($"Package declares more than {MaximumFileCount} files.");
        }

        long total = 0;
        List<PackageFileRecord> records = [];
        foreach (string file in files)
        {
            long length = new FileInfo(file).Length;
            if (length > MaximumSingleFileBytes)
            {
                errors.Add($"File '{RelativePathOf(directory, file)}' exceeds {MaximumSingleFileBytes} bytes.");
            }

            total += length;
            records.Add(new PackageFileRecord
            {
                RelativePath = RelativePathOf(directory, file),
                FullPath = file,
                Length = length,
            });
        }

        if (total > MaximumTotalUncompressedBytes)
        {
            errors.Add($"Package total {total} bytes exceeds {MaximumTotalUncompressedBytes}.");
        }

        if (!File.Exists(Path.Combine(directory, manifest!.EntryAssembly)))
        {
            errors.Add($"Entry assembly '{manifest.EntryAssembly}' is missing from the package root.");
        }

        foreach (PluginNativeHelper helper in manifest.NativeHelpers)
        {
            string entryPath = ResolvePackagePath(directory, helper.EntryPoint);
            if (!File.Exists(entryPath))
            {
                errors.Add($"Native helper '{helper.Id}' entry point '{helper.EntryPoint}' is missing.");
            }
            else if (!IsPe32Executable(entryPath))
            {
                errors.Add($"Native helper '{helper.Id}' entry point must be an x86 PE executable.");
            }

            foreach (string dependency in helper.Dependencies)
            {
                if (!File.Exists(ResolvePackagePath(directory, dependency)))
                {
                    errors.Add($"Native helper '{helper.Id}' dependency '{dependency}' is missing.");
                }
            }
        }

        result = new PackageValidationResult
        {
            Accepted = errors.Count == 0,
            Errors = errors,
            Manifest = manifest,
            Files = records,
            Digest = ComputeDirectoryDigest(directory),
        };
    }

    public static string ComputeDirectoryDigest(string directory)
    {
        using IncrementingHash hash = new();
        foreach (string file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            // Delimiters are legal file bytes, so hash a length-prefixed record rather than
            // concatenating paths and contents into an ambiguous byte stream.
            byte[] path = System.Text.Encoding.UTF8.GetBytes(RelativePathOf(directory, file));
            hash.AppendLength(path.Length);
            hash.Append(path);
            hash.AppendLength(new FileInfo(file).Length);
            using FileStream stream = File.OpenRead(file);
            hash.AppendStream(stream);
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public PackageValidationResult InstallDcPack(string dcpackPath, Func<string, bool> isOfficialNamespace)
        => InstallDcPack(dcpackPath, isOfficialNamespace, expectedDigest: null);

    public PackageValidationResult InspectDcPack(string dcpackPath, Func<string, bool> isOfficialNamespace)
    {
        ArgumentException.ThrowIfNullOrEmpty(dcpackPath);
        if (!File.Exists(dcpackPath))
            throw new PluginInstallException($"Package file '{dcpackPath}' does not exist.");

        string stagingRoot = Path.Combine(_pluginsRoot, "Inspection", $"{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingRoot);
        try
        {
            using ZipArchive archive = ZipFile.OpenRead(dcpackPath);
            ValidateAndExtract(archive, stagingRoot);
            ValidateExtractedPackage(stagingRoot, isOfficialNamespace, out PackageValidationResult result);
            return result;
        }
        finally
        {
            TryDeleteDirectory(stagingRoot);
        }
    }

    public PackageValidationResult InstallDcPack(string dcpackPath, Func<string, bool> isOfficialNamespace, string? expectedDigest)
    {
        ArgumentException.ThrowIfNullOrEmpty(dcpackPath);
        if (!File.Exists(dcpackPath))
        {
            throw new PluginInstallException($"Package file '{dcpackPath}' does not exist.");
        }

        string stagingRoot = Path.Combine(_pluginsRoot, "Staging", $"{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingRoot);
        try
        {
            using ZipArchive archive = ZipFile.OpenRead(dcpackPath);
            ValidateAndExtract(archive, stagingRoot);
            ValidateExtractedPackage(stagingRoot, isOfficialNamespace, out PackageValidationResult result);
            if (!result.Accepted)
            {
                return result;
            }

            if (expectedDigest is not null && !string.Equals(expectedDigest, result.Digest, StringComparison.OrdinalIgnoreCase))
            {
                throw new PluginInstallException("Package content changed after confirmation; installation was aborted.");
            }

            string targetDir = GetVersionDirectory(result.Manifest.Id, result.Manifest.Version);
            if (Directory.Exists(targetDir))
            {
                bool identical;
                try
                {
                    identical = string.Equals(ComputeDirectoryDigest(targetDir), result.Digest, StringComparison.OrdinalIgnoreCase);
                }
                catch (Exception)
                {
                    identical = false;
                }

                if (!identical)
                {
                    throw new PluginInstallException($"Version {result.Manifest.Version} of '{result.Manifest.Id}' is already installed with different content.");
                }

                return result;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetDir)!);
            Directory.Move(stagingRoot, targetDir);
            return result;
        }
        finally
        {
            try
            {
                if (Directory.Exists(stagingRoot))
                {
                    Directory.Delete(stagingRoot, recursive: true);
                }
            }
            catch (Exception)
            {
            }
        }
    }

    private static void ValidateAndExtract(ZipArchive archive, string destination)
    {
        long total = 0;
        HashSet<string> seenNames = new(StringComparer.OrdinalIgnoreCase);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            if (archive.Entries.Count > MaximumFileCount)
            {
                throw new PluginInstallException($"Package declares more than {MaximumFileCount} entries.");
            }

            string name = entry.FullName;
            if (string.IsNullOrEmpty(name) || name.Length > MaximumPathLength)
            {
                throw new PluginInstallException($"Zip entry name is empty or too long.");
            }

            if (name.Contains(':', StringComparison.Ordinal) || name.StartsWith('/') || name.StartsWith('\\')
                || name.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(name))
            {
                throw new PluginInstallException($"Zip entry '{name}' uses a forbidden path form.");
            }

            const uint unixModeBits = 0xFFFF0000;
            const uint symlinkMode = 0xA000;
            if ((((uint)entry.ExternalAttributes & unixModeBits) >> 16 & 0xF000) == symlinkMode)
            {
                throw new PluginInstallException($"Zip entry '{name}' is a symbolic link.");
            }

            if (entry.Length > MaximumSingleFileBytes)
            {
                throw new PluginInstallException($"Zip entry '{name}' declares {entry.Length} bytes, above the per-file limit.");
            }

            total += entry.Length;
            if (total > MaximumTotalUncompressedBytes)
            {
                throw new PluginInstallException("Package uncompressed size exceeds the total limit.");
            }

            if (!seenNames.Add(NormalizeEntryName(name)))
            {
                throw new PluginInstallException($"Zip contains a case-colliding duplicate entry '{name}'.");
            }

            string destinationPath = Path.GetFullPath(Path.Combine(destination, name));
            if (!destinationPath.StartsWith(Path.GetFullPath(destination) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new PluginInstallException($"Zip entry '{name}' escapes the extraction root.");
            }

            if (name.EndsWith("/", StringComparison.Ordinal) || name.EndsWith("\\", StringComparison.Ordinal)
                || entry.Name.Length == 0)
            {
                Directory.CreateDirectory(destinationPath);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            entry.ExtractToFile(destinationPath, overwrite: true);
            long extracted = new FileInfo(destinationPath).Length;
            if (extracted != entry.Length)
            {
                throw new PluginInstallException($"Zip entry '{name}' expanded inconsistently during extraction.");
            }
        }
    }

    private static string NormalizeEntryName(string name) => name.Replace('\\', '/').TrimEnd('/').ToLowerInvariant();

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception)
        {
        }
    }

    internal static string RelativePathOf(string directory, string file) => Path.GetRelativePath(directory, file).Replace('\\', '/');

    private static string ResolvePackagePath(string directory, string relativePath)
    {
        string root = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(Path.Combine(directory, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new PluginInstallException($"Package path '{relativePath}' escapes the package root.");
        }

        return path;
    }

    private static bool IsPe32Executable(string path)
    {
        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using BinaryReader reader = new(stream);
            if (stream.Length < 64 || reader.ReadUInt16() != 0x5A4D)
            {
                return false;
            }

            stream.Position = 0x3C;
            int peOffset = reader.ReadInt32();
            if (peOffset < 0 || peOffset > stream.Length - 24)
            {
                return false;
            }

            stream.Position = peOffset;
            return reader.ReadUInt32() == 0x00004550 && reader.ReadUInt16() == 0x014C;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private sealed class IncrementingHash : IDisposable
    {
        private readonly SHA256 _sha = SHA256.Create();

        public void AppendString(string value)
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(value);
            Append(bytes);
        }

        public void AppendLength(long length)
        {
            Span<byte> bytes = stackalloc byte[8];
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(bytes, length);
            Append(bytes);
        }

        public void Append(ReadOnlySpan<byte> bytes)
        {
            byte[] copy = bytes.ToArray();
            _sha.TransformBlock(copy, 0, copy.Length, null, 0);
        }

        public void AppendStream(Stream stream)
        {
            byte[] buffer = new byte[64 * 1024];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                _sha.TransformBlock(buffer, 0, read, null, 0);
            }
        }

        public byte[] GetHashAndReset()
        {
            _sha.TransformFinalBlock([], 0, 0);
            byte[] hash = _sha.Hash ?? [];
            return hash;
        }

        public void Dispose() => _sha.Dispose();
    }
}

