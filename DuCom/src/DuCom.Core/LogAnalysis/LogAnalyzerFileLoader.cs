namespace DuCom.Core.LogAnalysis;

public sealed record LogAnalyzerFileSource(string Path, string Role);

public sealed record LogAnalyzerFileLoadResult(
    IReadOnlyList<LogAnalyzerRecord> Records,
    long TotalLineCount,
    long TruncatedLineCount,
    IReadOnlyDictionary<string, string> SourceRoles);

public sealed record LogAnalyzerFileProgress(
    int FileIndex,
    int FileCount,
    string FileName,
    long ProcessedBytes,
    long TotalBytes,
    long ProcessedLines)
{
    public double Percentage => TotalBytes <= 0 ? 0 : Math.Clamp(ProcessedBytes * 100d / TotalBytes, 0, 100);
}

public static class LogAnalyzerFileLoader
{
    public static LogAnalyzerFileLoadResult Load(
        IReadOnlyList<LogAnalyzerFileSource> sources,
        LogAnalyzerParser parser,
        int maximumRecords,
        Func<long> nextSequence,
        IProgress<LogAnalyzerFileProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumRecords);
        Queue<LogAnalyzerRecord> records = new(maximumRecords);
        Dictionary<string, string> sourceRoles = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, BesSourceAnnotation> sourceAnnotations = new(StringComparer.OrdinalIgnoreCase);
        long totalBytes = sources.Sum(source => new FileInfo(source.Path).Length);
        long completedBytes = 0;
        long total = 0;
        for (int sourceIndex = 0; sourceIndex < sources.Count; sourceIndex++)
        {
            LogAnalyzerFileSource source = sources[sourceIndex];
            cancellationToken.ThrowIfCancellationRequested();
            DateTimeOffset fallback = File.GetLastWriteTime(source.Path);
            DateTimeOffset? previous = null;
            int dayOffset = 0;
            long lineNumber = 0;
            long fileLength = new FileInfo(source.Path).Length;
            using FileStream stream = new(source.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using StreamReader reader = new(stream, detectEncodingFromByteOrderMarks: true);
            while (reader.ReadLine() is { } line)
            {
                cancellationToken.ThrowIfCancellationRequested();
                total++;
                DateTimeOffset received = fallback.AddDays(dayOffset).AddTicks(lineNumber++);
                LogAnalyzerRecord record = parser.Parse(nextSequence(), source.Path, Path.GetFileName(source.Path), source.Role,
                    received, TimeSpan.Zero, line);
                if (previous is { } prior && record.BaseDisplayTime.TimeOfDay < prior.TimeOfDay &&
                    prior.TimeOfDay - record.BaseDisplayTime.TimeOfDay > TimeSpan.FromHours(12))
                {
                    dayOffset++;
                    record = record with
                    {
                        BaseDisplayTime = record.BaseDisplayTime.AddDays(1),
                        DisplayTime = record.DisplayTime.AddDays(1),
                    };
                }
                previous = record.BaseDisplayTime;
                records.Enqueue(record);
                if (records.Count > maximumRecords) records.Dequeue();
                BesSourceAnnotation detected = BesSourceAnnotationDetector.Detect(line);
                BesSourceAnnotation previousAnnotation = sourceAnnotations.GetValueOrDefault(source.Path, new BesSourceAnnotation(null, null));
                BesSourceAnnotation annotation = new(detected.Side ?? previousAnnotation.Side, detected.TwsRole ?? previousAnnotation.TwsRole);
                sourceAnnotations[source.Path] = annotation;
                if (!string.IsNullOrEmpty(annotation.Display)) sourceRoles[source.Path] = annotation.Display;
                if ((lineNumber & 0x3FF) == 0)
                    progress?.Report(new LogAnalyzerFileProgress(sourceIndex + 1, sources.Count, Path.GetFileName(source.Path),
                        completedBytes + Math.Min(stream.Position, fileLength), totalBytes, total));
            }
            completedBytes += fileLength;
            progress?.Report(new LogAnalyzerFileProgress(sourceIndex + 1, sources.Count, Path.GetFileName(source.Path), completedBytes, totalBytes, total));
        }
        LogAnalyzerRecord[] adjusted = records.Select(record => record with
        {
            SourceRole = sourceRoles.GetValueOrDefault(record.SourceId, record.SourceRole),
        }).ToArray();
        return new LogAnalyzerFileLoadResult(adjusted, total, Math.Max(0, total - records.Count), sourceRoles);
    }
}
