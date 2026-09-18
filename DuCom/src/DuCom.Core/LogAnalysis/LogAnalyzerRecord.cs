namespace DuCom.Core.LogAnalysis;

public sealed record LogAnalyzerRecord(
    long Sequence,
    string SourceId,
    string SourceName,
    string SourceRole,
    DateTimeOffset ReceivedAt,
    DateTimeOffset BaseDisplayTime,
    DateTimeOffset DisplayTime,
    string Level,
    string Module,
    string Message,
    string OriginalText,
    IReadOnlyList<LogAnalyzerRule> MatchedRules,
    IReadOnlyList<BluetoothHciAnalysis>? ProtocolAnalyses = null)
{
    public IReadOnlyList<BluetoothHciAnalysis> BluetoothAnalyses { get; } = ProtocolAnalyses ?? [];

    public string Keywords { get; } = string.Join(", ", MatchedRules.Where(rule => rule.IncludeInAnalysis).Select(rule => rule.Name)
        .Concat((ProtocolAnalyses ?? []).Select(analysis => analysis.Keyword))
        .Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase));

    public string ChineseComment { get; } = BuildChineseComment(MatchedRules, ProtocolAnalyses ?? []);

    private static string BuildChineseComment(IReadOnlyList<LogAnalyzerRule> matchedRules, IReadOnlyList<BluetoothHciAnalysis> analyses)
    {
        string comments = string.Join("；", analyses.Select(analysis => analysis.Comment).Concat(matchedRules.Where(rule => rule.IncludeInAnalysis).Select(rule => rule.Comment))
            .Where(comment => !string.IsNullOrWhiteSpace(comment)).Distinct(StringComparer.OrdinalIgnoreCase));
        return comments;
    }
}
