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
        BluetoothHciAnalysis[] bluetoothAnalyses = DecodeProtocols(text, message)
            .DistinctBy(analysis => (analysis.Module.ToUpperInvariant(), analysis.Keyword.ToUpperInvariant(), analysis.Comment.ToUpperInvariant()))
            .ToArray();
        if (bluetoothAnalyses.Length > 0)
        {
            IEnumerable<string> modules = string.IsNullOrWhiteSpace(module) || string.Equals(module, "NONE", StringComparison.OrdinalIgnoreCase)
                ? bluetoothAnalyses.Select(analysis => analysis.Module)
                : new[] { module }.Concat(bluetoothAnalyses.Select(analysis => analysis.Module));
            module = string.Join(" / ", modules.Distinct(StringComparer.OrdinalIgnoreCase));
        }

        if (string.IsNullOrEmpty(level))
        {
            level = bluetoothAnalyses.Any(BesFaultDecoder.IsFatal)
                ? "ERROR"
                : bluetoothAnalyses.Any(BesFaultDecoder.IsWarning)
                ? "WARN"
                : matches.Where(rule => rule.IncludeInAnalysis).Any(rule =>
                    ContainsSeverity(rule.Pattern, "ASSERT", "EXCEPTION", "ERROR", "FATAL", "PANIC"))
                ? "ERROR"
                : matches.Where(rule => rule.IncludeInAnalysis).Any(rule => ContainsSeverity(rule.Pattern, "WARNING", "WARN")) ? "WARN" : "OTHER";
        }

        return new LogAnalyzerRecord(sequence, sourceId, sourceName, sourceRole, receivedAt, baseDisplayTime, baseDisplayTime + timeOffset,
            level, module, message, text, matches, bluetoothAnalyses);
    }

    private static BluetoothHciAnalysis[] DecodeProtocols(string text, string message)
    {
        try
        {
            return BluetoothHciDecoder.DecodeAll(text)
                .Concat(BluetoothProfileDecoder.DecodeAll(message))
                .Concat(BesStartupDecoder.DecodeAll(message))
                .Concat(BesRuntimeDecoder.DecodeAll(message))
                .Concat(BesFaultDecoder.DecodeAll(message))
                .ToArray();
        }
        catch (Exception exception) when (exception is RegexMatchTimeoutException or FormatException or OverflowException)
        {
            return [];
        }
    }

    private static bool ContainsSeverity(string pattern, params string[] values) =>
        values.Any(value => pattern.Contains(value, StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex(@"^(?:\[(?<hostTs>\d{2}:\d{2}:\d{2}\.\d{3})\]\s)?\s*(?<tick>\d+)/(?<level>[IWE])/(?<module>\S+)\s*/\s*(?<cpu>\d+)\s*\|\s?(?<msg>.*)$", RegexOptions.CultureInvariant, 100)]
    private static partial Regex BesTraceRegex();

    [GeneratedRegex(@"^\[(?<hostTs>\d{2}:\d{2}:\d{2}\.\d{3})\]\s?", RegexOptions.CultureInvariant, 100)]
    private static partial Regex HostTimestampRegex();
}
