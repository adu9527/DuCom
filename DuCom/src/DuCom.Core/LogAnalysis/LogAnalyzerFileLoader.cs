namespace DuCom.Core.LogAnalysis;

public sealed record LogAnalyzerFileSource(string Path, string Role);

public sealed record LogAnalyzerFileLoadResult(
    IReadOnlyList<LogAnalyzerRecord> Records,
    long TotalLineCount,
    long TruncatedLineCount);

public static class LogAnalyzerFileLoader
{
    public static LogAnalyzerFileLoadResult Load(
        IReadOnlyList<LogAnalyzerFileSource> sources,
        LogAnalyzerParser parser,
        int maximumRecords,
        Func<long> nextSequence)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumRecords);
        Queue<LogAnalyzerRecord> records = new(maximumRecords);
        long total = 0;
        foreach (LogAnalyzerFileSource source in sources)
        {
            DateTimeOffset fallback = File.GetLastWriteTime(source.Path);
            DateTimeOffset? previous = null;
            int dayOffset = 0;
            long lineNumber = 0;
            foreach (string line in File.ReadLines(source.Path))
            {
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
            }
        }
        return new LogAnalyzerFileLoadResult([.. records], total, Math.Max(0, total - records.Count));
    }
}
