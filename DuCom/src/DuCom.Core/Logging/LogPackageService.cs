using System.IO;
using System.IO.Compression;
using System.Text;
namespace DuCom.Core.Logging;

public sealed record LogPackagePort(
    string PortName,
    string DeviceName,
    IReadOnlyList<SessionLogFileSnapshot> Files);

public sealed record LogPackageRequest(
    string OutputDirectory,
    string ProjectName,
    string Title,
    string Tester,
    string DeviceSoftwareVersion,
    string ReproductionProbability,
    string ReproductionTime,
    string ProblemDescription,
    string ReproductionSteps,
    string Notes,
    DateTimeOffset PackageTime,
    IReadOnlyList<LogPackagePort> Ports);

public static class LogPackageService
{
    public const long LargeFileWarningBytes = 60L * 1024 * 1024;

    public static async Task<string> CreateAsync(LogPackageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Directory.CreateDirectory(request.OutputDirectory);
        string baseName = SanitizeFileName($"{request.ProjectName}-{request.Title}-{request.Tester}-{request.PackageTime:yyyyMMdd-HHmmss-fff}");
        string targetPath = GetAvailablePath(request.OutputDirectory, baseName, ".zip");
        string temporaryPath = targetPath + ".tmp";

        try
        {
            await using FileStream zipStream = new(temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 64 * 1024, FileOptions.Asynchronous);
            using ZipArchive archive = new(zipStream, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8);
            await WriteDescriptionAsync(archive, request, cancellationToken).ConfigureAwait(false);

            foreach (LogPackagePort port in request.Ports)
            {
                for (int index = 0; index < port.Files.Count; index++)
                {
                    SessionLogFileSnapshot file = port.Files[index];
                    string extension = Path.GetExtension(file.Path);
                    string originalStem = SanitizeFileName(Path.GetFileNameWithoutExtension(file.Path));
                    string deviceSuffix = string.IsNullOrWhiteSpace(port.DeviceName)
                        ? string.Empty
                        : $"-{SanitizeFileName(port.DeviceName)}";
                    string entryName = $"日志/{originalStem}{deviceSuffix}{extension}";
                    ZipArchiveEntry entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
                    await using Stream destination = entry.Open();
                    await using FileStream source = new(file.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    await CopyExactlyAsync(source, destination, file.Length, cancellationToken).ConfigureAwait(false);
                }
            }

            archive.Dispose();
            await zipStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            await zipStream.DisposeAsync().ConfigureAwait(false);
            File.Move(temporaryPath, targetPath);
            return targetPath;
        }
        catch
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }

            throw;
        }
    }

    public static string SanitizeFileName(string value)
    {
        string sanitized = string.Concat((value ?? string.Empty).Trim().Select(character =>
            Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
        sanitized = sanitized.TrimEnd('.', ' ');
        if (sanitized.Length > 160)
        {
            sanitized = sanitized[..160].TrimEnd('.', ' ');
        }
        return string.IsNullOrWhiteSpace(sanitized) ? "DuCom日志包" : sanitized;
    }

    private static async Task WriteDescriptionAsync(ZipArchive archive, LogPackageRequest request, CancellationToken cancellationToken)
    {
        ZipArchiveEntry entry = archive.CreateEntry("问题详细描述.txt", CompressionLevel.Optimal);
        await using Stream stream = entry.Open();
        await using StreamWriter writer = new(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), leaveOpen: false);
        await writer.WriteAsync($"项目名称：{request.ProjectName}\r\n").ConfigureAwait(false);
        await writer.WriteAsync($"问题标题：{request.Title}\r\n").ConfigureAwait(false);
        await writer.WriteAsync($"测试人员：{request.Tester}\r\n").ConfigureAwait(false);
        await writer.WriteAsync($"设备软件版本：{request.DeviceSoftwareVersion}\r\n").ConfigureAwait(false);
        await writer.WriteAsync($"复现概率：{request.ReproductionProbability}\r\n").ConfigureAwait(false);
        await writer.WriteAsync($"复现时间：{request.ReproductionTime}\r\n").ConfigureAwait(false);
        await writer.WriteAsync($"打包时间：{request.PackageTime:yyyy-MM-dd HH:mm:ss.fff}\r\n\r\n").ConfigureAwait(false);
        await writer.WriteAsync("设备映射：\r\n").ConfigureAwait(false);
        foreach (LogPackagePort port in request.Ports)
        {
            await writer.WriteAsync($"{port.PortName}：{port.DeviceName}\r\n").ConfigureAwait(false);
        }

        await writer.WriteAsync($"\r\n问题现象：\r\n{request.ProblemDescription}\r\n\r\n复现步骤：\r\n{request.ReproductionSteps}\r\n\r\n备注：\r\n{request.Notes}\r\n").ConfigureAwait(false);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task CopyExactlyAsync(Stream source, Stream destination, long length, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[64 * 1024];
        long remaining = length;
        while (remaining > 0)
        {
            int read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("The log file became shorter while the package snapshot was being copied.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            remaining -= read;
        }
    }

    private static string GetAvailablePath(string directory, string baseName, string extension)
    {
        string path = Path.Combine(directory, baseName + extension);
        for (int collision = 1; File.Exists(path) || File.Exists(path + ".tmp"); collision++)
        {
            path = Path.Combine(directory, $"{baseName}-{collision:D2}{extension}");
        }

        return path;
    }
}
