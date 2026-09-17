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
    IReadOnlyList<LogAnalyzerRule> MatchedRules)
{
    public string Keywords { get; } = string.Join(", ", MatchedRules.Select(rule => rule.Name).Distinct(StringComparer.OrdinalIgnoreCase));

    public string ChineseComment { get; } = BuildChineseComment(MatchedRules, Level, Module);

    private static string BuildChineseComment(IReadOnlyList<LogAnalyzerRule> matchedRules, string level, string module)
    {
        string comments = string.Join("；", matchedRules.Select(rule => rule.Comment)
            .Where(comment => !string.IsNullOrWhiteSpace(comment)).Distinct(StringComparer.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(comments)) return comments;
        return level switch
        {
            "ERROR" => string.IsNullOrEmpty(module) ? "错误日志" : $"{module} 模块错误日志",
            "WARN" => string.IsNullOrEmpty(module) ? "警告日志" : $"{module} 模块警告日志",
            "INFO" => string.IsNullOrEmpty(module) ? "信息日志" : $"{module} 模块运行日志",
            _ when !string.IsNullOrEmpty(module) => $"{module} 模块日志",
            _ => "未匹配分析规则",
        };
    }
}
