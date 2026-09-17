namespace DuCom.Core.LogAnalysis;

public sealed record LogAnalyzerAggregation(
    IReadOnlyDictionary<string, int> Sources,
    IReadOnlyDictionary<string, int> Levels,
    IReadOnlyDictionary<string, int> Categories,
    IReadOnlyDictionary<Guid, int> Rules);

public static class LogAnalyzerOperations
{
    public static LogAnalyzerAggregation Aggregate(IEnumerable<LogAnalyzerRecord> records)
    {
        Dictionary<string, int> sources = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int> levels = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int> categories = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<Guid, int> rules = [];
        foreach (LogAnalyzerRecord record in records)
        {
            sources[record.SourceName] = sources.GetValueOrDefault(record.SourceName) + 1;
            levels[record.Level] = levels.GetValueOrDefault(record.Level) + 1;
            foreach (LogAnalyzerRule rule in record.MatchedRules)
            {
                categories[rule.Category] = categories.GetValueOrDefault(rule.Category) + 1;
                rules[rule.Id] = rules.GetValueOrDefault(rule.Id) + 1;
            }
        }
        return new LogAnalyzerAggregation(sources, levels, categories, rules);
    }

    public static bool MatchesSearch(LogAnalyzerRecord record, string search)
    {
        if (string.IsNullOrWhiteSpace(search)) return true;
        return record.OriginalText.Contains(search, StringComparison.OrdinalIgnoreCase)
            || record.SourceName.Contains(search, StringComparison.OrdinalIgnoreCase)
            || record.SourceRole.Contains(search, StringComparison.OrdinalIgnoreCase)
            || record.Level.Contains(search, StringComparison.OrdinalIgnoreCase)
            || record.Module.Contains(search, StringComparison.OrdinalIgnoreCase)
            || record.Keywords.Contains(search, StringComparison.OrdinalIgnoreCase)
            || record.ChineseComment.Contains(search, StringComparison.OrdinalIgnoreCase);
    }
}
