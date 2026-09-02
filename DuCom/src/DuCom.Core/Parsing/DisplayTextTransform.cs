using System.Globalization;
using System.Text.RegularExpressions;

namespace DuCom.Core.Parsing;

/// <summary>
/// Pure display-text transforms for the log view: making invisible characters visible and
/// converting timestamp tokens to local time. Display-layer only; never applied to log files.
/// </summary>
public static partial class DisplayTextTransform
{
    public const char SpaceSubstitute = '·';
    public const char TabSubstitute = '→';
    public const string CarriageReturnSubstitute = "␍";
    public const string LineFeedSubstitute = "␊";

    [GeneratedRegex(@"\b(\d{4}-\d{2}-\d{2})T(\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?)(Z|[+-]\d{2}:\d{2})?\b")]
    private static partial Regex IsoTimestampRegex();

    [GeneratedRegex(@"(?<![\d.])(1[5-9]\d{11})(?![\d.])")]
    private static partial Regex UnixMillisecondsRegex();

    public static string Apply(string text, bool showControlCharacters, bool showSpaces, bool showTabs)
    {
        if (text.Length == 0 || (!showControlCharacters && !showSpaces && !showTabs))
        {
            return text;
        }

        if (showControlCharacters && (text.Contains('\r') || text.Contains('\n')))
        {
            text = text.Replace("\r\n", CarriageReturnSubstitute + LineFeedSubstitute, StringComparison.Ordinal)
                .Replace("\r", CarriageReturnSubstitute, StringComparison.Ordinal)
                .Replace("\n", LineFeedSubstitute, StringComparison.Ordinal);
        }

        if (showSpaces)
        {
            text = text.Replace(' ', SpaceSubstitute);
        }

        if (showTabs)
        {
            text = text.Replace('\t', TabSubstitute);
        }

        return text;
    }

    /// <summary>
    /// Converts ISO-8601 timestamps (UTC "Z" or explicit offset) and standalone 13-digit
    /// Unix millisecond tokens to local "yyyy-MM-dd HH:mm:ss.fff". Text without matching
    /// tokens is returned unchanged.
    /// </summary>
    public static string TimestampsToLocal(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        text = IsoTimestampRegex().Replace(text, match =>
        {
            string offset = match.Groups[3].Value;
            if (offset.Length == 0 || offset == "Z" || offset == "z")
            {
                // No offset or UTC: convert to local.
                if (DateTimeOffset.TryParse(match.Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset parsed))
                {
                    return parsed.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
                }

                return match.Value;
            }

            return match.Value; // Already carries an explicit offset; leave as written.
        });

        text = UnixMillisecondsRegex().Replace(text, match =>
        {
            long milliseconds = long.Parse(match.Groups[1].ValueSpan, CultureInfo.InvariantCulture);
            DateTimeOffset local = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).ToLocalTime();
            return local.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
        });

        return text;
    }
}
