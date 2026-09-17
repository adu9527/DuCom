using System.Globalization;
using System.Text.RegularExpressions;

namespace DuCom.Core.LogAnalysis;

public sealed partial class LogAnalyzerParser(IReadOnlyList<LogAnalyzerRule> rules)
{
    public LogAnalyzerRecord Parse(
        long sequence,
        string sourceId,
        string sourceName,
        string sourceRole,
        DateTimeOffset receivedAt,
        TimeSpan timeOffset,
        string text)
    {
        Match trace = BesTraceRegex().Match(text);
        string level = string.Empty;
        string module = string.Empty;
        string message = text;
        DateTimeOffset baseDisplayTime = receivedAt;

        if (trace.Success)
        {
            level = trace.Groups["level"].Value switch
            {
                "I" => "INFO",
                "W" => "WARN",
                "E" => "ERROR",
                _ => string.Empty,
            };
            module = trace.Groups["module"].Value;
            message = trace.Groups["msg"].Value;
            if (trace.Groups["hostTs"].Success &&
                TimeSpan.TryParseExact(trace.Groups["hostTs"].Value, "hh\\:mm\\:ss\\.fff", CultureInfo.InvariantCulture, out TimeSpan time))
            {
                baseDisplayTime = new DateTimeOffset(receivedAt.Date + time, receivedAt.Offset);
            }
        }
        else
        {
            Match host = HostTimestampRegex().Match(text);
            if (host.Success)
            {
                message = text[host.Length..];
                if (TimeSpan.TryParseExact(host.Groups["hostTs"].Value, "hh\\:mm\\:ss\\.fff", CultureInfo.InvariantCulture, out TimeSpan time))
                {
                    baseDisplayTime = new DateTimeOffset(receivedAt.Date + time, receivedAt.Offset);
                }
            }
        }

        LogAnalyzerRule[] matches = rules
            .Where(rule => rule.IsEnabled && !string.IsNullOrEmpty(rule.Pattern) &&
                text.Contains(rule.Pattern, rule.IsCaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (string.IsNullOrEmpty(level))
        {
            level = matches.Any(rule => rule.Pattern.Contains("ASSERT", StringComparison.OrdinalIgnoreCase) ||
                                        rule.Pattern.Contains("EXCEPTION", StringComparison.OrdinalIgnoreCase))
                ? "ERROR"
                : matches.Any(rule => rule.Pattern.Contains("WARNING", StringComparison.OrdinalIgnoreCase)) ? "WARN" : "OTHER";
        }

        return new LogAnalyzerRecord(sequence, sourceId, sourceName, sourceRole, receivedAt, baseDisplayTime, baseDisplayTime + timeOffset,
            level, module, message, text, matches);
    }

    [GeneratedRegex(@"^(?:\[(?<hostTs>\d{2}:\d{2}:\d{2}\.\d{3})\]\s)?\s*(?<tick>\d+)/(?<level>[IWE])/(?<module>\S+)\s*/\s*(?<cpu>\d+)\s*\|\s?(?<msg>.*)$", RegexOptions.CultureInvariant, 100)]
    private static partial Regex BesTraceRegex();

    [GeneratedRegex(@"^\[(?<hostTs>\d{2}:\d{2}:\d{2}\.\d{3})\]\s?", RegexOptions.CultureInvariant, 100)]
    private static partial Regex HostTimestampRegex();
}
