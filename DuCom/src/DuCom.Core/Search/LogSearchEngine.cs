using System.Text.RegularExpressions;
using DuCom.Core.Storage;

namespace DuCom.Core.Search;

public static class LogSearchEngine
{
    private const RegexOptions RegexOptionsBase = RegexOptions.ExplicitCapture | RegexOptions.CultureInvariant;

    /// <summary>
    /// Unified per-line regex timeout. A catastrophic pattern aborts the search pass with
    /// <see cref="RegexTimeoutMessage"/> instead of faulting the background task.
    /// </summary>
    public static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

    /// <summary>Stable marker returned in <see cref="SearchResult.ErrorMessage"/> after a regex timeout.</summary>
    public const string RegexTimeoutMessage = "regex-timeout";

    public static SearchResult Search(LineStoreSnapshot snapshot, SearchRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrEmpty(request.Pattern))
        {
            return SearchResult.Empty;
        }

        if (request.UseRegex)
        {
            return SearchRegex(snapshot, request, cancellationToken);
        }

        return SearchText(snapshot, request, cancellationToken);
    }

    private static SearchResult SearchText(LineStoreSnapshot snapshot, SearchRequest request, CancellationToken cancellationToken)
    {
        StringComparison comparison = request.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        List<SearchMatch> matches = [];

        foreach (StoredLine line in snapshot.Lines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string text = line.Text;
            if (request.MatchWholeLine)
            {
                if (string.Equals(text, request.Pattern, comparison))
                {
                    matches.Add(new SearchMatch(line.LogicalId, line.SegmentIndex, 0, text.Length));
                }

                continue;
            }

            int startIndex = 0;

            while (startIndex < text.Length)
            {
                int index = text.IndexOf(request.Pattern, startIndex, comparison);
                if (index < 0)
                {
                    break;
                }

                matches.Add(new SearchMatch(line.LogicalId, line.SegmentIndex, index, request.Pattern.Length));
                startIndex = index + request.Pattern.Length;
            }
        }

        return new SearchResult(matches, null, false);
    }

    private static SearchResult SearchRegex(LineStoreSnapshot snapshot, SearchRequest request, CancellationToken cancellationToken)
    {
        RegexOptions options = RegexOptionsBase;
        if (!request.MatchCase)
        {
            options |= RegexOptions.IgnoreCase;
        }

        Regex regex;
        try
        {
            regex = new Regex(request.Pattern, options, MatchTimeout);
        }
        catch (ArgumentException exception)
        {
            return new SearchResult([], exception.Message, false);
        }

        List<SearchMatch> matches = [];
        try
        {
            foreach (StoredLine line in snapshot.Lines)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string text = line.Text;

                foreach (Match match in regex.Matches(text).Cast<Match>())
                {
                    if (!match.Success)
                    {
                        continue;
                    }

                    if (request.MatchWholeLine && (match.Index != 0 || match.Length != text.Length))
                    {
                        continue;
                    }

                    matches.Add(new SearchMatch(line.LogicalId, line.SegmentIndex, match.Index, match.Length));
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // The pattern is pathological for at least one line; continuing would burn the
            // same budget on every remaining line. Abort with a stable, localizable marker.
            return new SearchResult([], RegexTimeoutMessage, false);
        }

        return new SearchResult(matches, null, false);
    }
}
