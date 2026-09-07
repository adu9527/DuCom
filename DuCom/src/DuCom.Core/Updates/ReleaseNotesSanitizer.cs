using System.Text.RegularExpressions;

namespace DuCom.Core.Updates;

/// <summary>
/// Converts a GitHub release body (Markdown with embedded HTML such as image tags)
/// into readable plain text for the update window.
/// </summary>
public static partial class ReleaseNotesSanitizer
{
    [GeneratedRegex(@"<img[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HtmlImageRegex();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.CultureInvariant)]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"!\[[^\]]*\]\([^)]*\)", RegexOptions.CultureInvariant)]
    private static partial Regex MarkdownImageRegex();

    [GeneratedRegex(@"\[([^\]]*)\]\([^)]*\)", RegexOptions.CultureInvariant)]
    private static partial Regex MarkdownLinkRegex();

    [GeneratedRegex(@"(\*\*|__|\*|~~|`)", RegexOptions.CultureInvariant)]
    private static partial Regex InlineEmphasisRegex();

    [GeneratedRegex(@"^\s{0,3}(#{1,6}\s+|>\s?|[-*+]\s+)", RegexOptions.CultureInvariant)]
    private static partial Regex LinePrefixRegex();

    [GeneratedRegex(@"^\s*\d+[.)]\s+", RegexOptions.CultureInvariant)]
    private static partial Regex NumberedListRegex();

    [GeneratedRegex(@"^\s{0,3}([-*_]\s*){3,}$", RegexOptions.CultureInvariant)]
    private static partial Regex HorizontalRuleRegex();

    public static string ToPlainText(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return string.Empty;
        }

        string normalized = markdown.Replace("\r\n", "\n").Trim();
        List<string> keptLines = [];
        bool previousLineWasBlank = false;
        foreach (string line in normalized.Split('\n'))
        {
            bool sourceLineWasBlank = string.IsNullOrWhiteSpace(line);
            string cleaned = CleanLine(line);
            if (!sourceLineWasBlank && string.IsNullOrWhiteSpace(cleaned))
            {
                continue;
            }

            bool isBlank = string.IsNullOrWhiteSpace(cleaned);
            if (isBlank)
            {
                if (previousLineWasBlank)
                {
                    continue;
                }

                previousLineWasBlank = true;
                keptLines.Add(string.Empty);
            }
            else
            {
                previousLineWasBlank = false;
                keptLines.Add(cleaned.TrimEnd());
            }
        }

        while (keptLines.Count > 0 && keptLines[^1].Length == 0)
        {
            keptLines.RemoveAt(keptLines.Count - 1);
        }

        return string.Join('\n', keptLines);
    }

    private static string CleanLine(string line)
    {
        string cleaned = HtmlImageRegex().Replace(line, string.Empty);
        cleaned = MarkdownImageRegex().Replace(cleaned, string.Empty);
        cleaned = MarkdownLinkRegex().Replace(cleaned, "$1");
        cleaned = InlineEmphasisRegex().Replace(cleaned, string.Empty);
        cleaned = HtmlTagRegex().Replace(cleaned, string.Empty);
        cleaned = DecodeHtmlEntities(cleaned);
        if (HorizontalRuleRegex().IsMatch(cleaned))
        {
            return string.Empty;
        }

        cleaned = LinePrefixRegex().Replace(cleaned, string.Empty);
        cleaned = NumberedListRegex().Replace(cleaned, string.Empty);
        return cleaned;
    }

    private static string DecodeHtmlEntities(string text) => text
        .Replace("&amp;", "&", StringComparison.Ordinal)
        .Replace("&lt;", "<", StringComparison.Ordinal)
        .Replace("&gt;", ">", StringComparison.Ordinal)
        .Replace("&quot;", "\"", StringComparison.Ordinal)
        .Replace("&#39;", "'", StringComparison.Ordinal)
        .Replace("&nbsp;", " ", StringComparison.Ordinal);
}
