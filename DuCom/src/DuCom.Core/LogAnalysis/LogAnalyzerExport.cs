using System.Text;

namespace DuCom.Core.LogAnalysis;

public sealed record LogAnalyzerExportResult(string SourcePath, string OutputPath, long TotalLines, long AnnotatedLines);

public static class LogAnalyzerExport
{
    private const int CommentColumn = 100;

    public static async Task<LogAnalyzerExportResult> ExportAnnotatedCopyAsync(
        string sourcePath,
        LogAnalyzerParser parser,
        CancellationToken cancellationToken = default)
    {
        string outputPath = GetOutputPath(sourcePath);
        string temporaryPath = outputPath + ".tmp";
        long totalLines = 0;
        long annotatedLines = 0;
        try
        {
            await WriteAnnotatedCopyAsync();
            File.Move(temporaryPath, outputPath, overwrite: true);
            return new LogAnalyzerExportResult(sourcePath, outputPath, totalLines, annotatedLines);

            async Task WriteAnnotatedCopyAsync()
            {
                await using FileStream input = new(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, useAsync: true);
                using StreamReader reader = new(input, detectEncodingFromByteOrderMarks: true);
                await using FileStream output = new(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);
                await using StreamWriter writer = new(output, new UTF8Encoding(false));
                DateTimeOffset fallback = File.GetLastWriteTime(sourcePath);
                while (await reader.ReadLineAsync(cancellationToken) is { } line)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    totalLines++;
                    LogAnalyzerRecord record = parser.Parse(totalLines, sourcePath, Path.GetFileName(sourcePath), string.Empty,
                        fallback.AddTicks(totalLines), TimeSpan.Zero, line);
                    if (string.IsNullOrWhiteSpace(record.ChineseComment))
                    {
                        await writer.WriteLineAsync(line.AsMemory(), cancellationToken);
                        continue;
                    }

                    int padding = Math.Max(4, CommentColumn - line.Length);
                    await writer.WriteAsync(line.AsMemory(), cancellationToken);
                    await writer.WriteAsync(new string(' ', padding).AsMemory(), cancellationToken);
                    await writer.WriteAsync("//".AsMemory(), cancellationToken);
                    await writer.WriteLineAsync(record.ChineseComment.AsMemory(), cancellationToken);
                    annotatedLines++;
                }
                await writer.FlushAsync(cancellationToken);
            }
        }
        catch
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            throw;
        }
    }

    public static async Task<IReadOnlyList<LogAnalyzerExportResult>> ExportAnnotatedCopiesAsync(
        IReadOnlyList<string> sourcePaths,
        LogAnalyzerParser parser,
        IProgress<(int Index, int Count, string SourcePath)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourcePaths);
        ArgumentNullException.ThrowIfNull(parser);
        List<LogAnalyzerExportResult> results = new(sourcePaths.Count);
        for (int index = 0; index < sourcePaths.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string sourcePath = sourcePaths[index];
            progress?.Report((index + 1, sourcePaths.Count, sourcePath));
            results.Add(await ExportAnnotatedCopyAsync(sourcePath, parser, cancellationToken));
        }
        return results;
    }

    public static string GetOutputPath(string sourcePath)
    {
        string directory = Path.GetDirectoryName(sourcePath) ?? string.Empty;
        string name = Path.GetFileNameWithoutExtension(sourcePath);
        string extension = Path.GetExtension(sourcePath);
        return Path.Combine(directory, name + "-analyse" + extension);
    }
}
