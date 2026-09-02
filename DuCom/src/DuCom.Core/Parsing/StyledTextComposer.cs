namespace DuCom.Core.Parsing;

/// <summary>
/// Merges ANSI-styled runs and highlight-rule runs over the same clean display text into
/// final styled runs. Precedence: an explicit ANSI foreground wins over a highlight-rule
/// foreground; backgrounds only come from ANSI. Reverse video with both explicit colors is
/// resolved by swapping; otherwise the run keeps an <see cref="StyleRun.Inverse"/> marker.
/// Adjacent pieces with identical resolved styles are merged to keep run counts low.
/// Inputs must cover the same clean display text; mismatches degrade safely to unstyled text.
/// </summary>
public static class StyledTextComposer
{
    public static IReadOnlyList<StyleRun> Compose(
        IReadOnlyList<AnsiRun> ansiRuns,
        IReadOnlyList<HighlightRun> highlightRuns)
    {
        int totalLength = TotalLength(ansiRuns);
        if (totalLength == 0)
        {
            return [new StyleRun(string.Empty, null, null, null, null, null, null, false, false, false)];
        }

        HighlightSegment[] coverage = BuildCoverage(highlightRuns, totalLength);

        List<StyleRun> result = [];
        int textPosition = 0;
        foreach (AnsiRun ansiRun in ansiRuns)
        {
            string runText = ansiRun.Text;
            int consumed = 0;
            while (consumed < runText.Length)
            {
                int boundary = FindCoverageBoundary(coverage, textPosition);
                int pieceLength = Math.Min(runText.Length - consumed, boundary - textPosition);
                if (pieceLength <= 0)
                {
                    pieceLength = runText.Length - consumed;
                }

                HighlightSegment? cover = FindCoverage(coverage, textPosition);
                byte? highlightR = null;
                byte? highlightG = null;
                byte? highlightB = null;
                byte? highlightBackgroundR = null;
                byte? highlightBackgroundG = null;
                byte? highlightBackgroundB = null;
                if (cover is { HasColoring: true })
                {
                    if (cover.Value.HasForeground)
                    {
                        highlightR = cover.Value.ForegroundR;
                        highlightG = cover.Value.ForegroundG;
                        highlightB = cover.Value.ForegroundB;
                    }

                    if (cover.Value.HasBackground)
                    {
                        highlightBackgroundR = cover.Value.BackgroundR;
                        highlightBackgroundG = cover.Value.BackgroundG;
                        highlightBackgroundB = cover.Value.BackgroundB;
                    }
                }

                result.Add(ResolveRun(
                    ansiRun.Style,
                    runText.Substring(consumed, pieceLength),
                    highlightR,
                    highlightG,
                    highlightB,
                    highlightBackgroundR,
                    highlightBackgroundG,
                    highlightBackgroundB,
                    cover?.Bold ?? false,
                    cover?.Italic ?? false));
                consumed += pieceLength;
                textPosition += pieceLength;
            }
        }

        MergeAdjacent(result);
        return result;
    }

    private static int TotalLength(IReadOnlyList<AnsiRun> runs)
    {
        int total = 0;
        foreach (AnsiRun run in runs)
        {
            total += run.Text.Length;
        }

        return total;
    }

    private static HighlightSegment[] BuildCoverage(IReadOnlyList<HighlightRun> highlightRuns, int totalLength)
    {
        List<HighlightSegment> segments = [];
        int position = 0;
        foreach (HighlightRun run in highlightRuns)
        {
            if (run.Text.Length == 0)
            {
                continue;
            }

            int end = Math.Min(position + run.Text.Length, totalLength);
            segments.Add(new HighlightSegment(
                position,
                end,
                run.ForegroundR,
                run.ForegroundG,
                run.ForegroundB,
                run.BackgroundR,
                run.BackgroundG,
                run.BackgroundB,
                run.Bold,
                run.Italic));
            position += run.Text.Length;
            if (position >= totalLength)
            {
                break;
            }
        }

        return [.. segments];
    }

    /// <summary>Returns the absolute offset where the current highlight coverage changes.</summary>
    private static int FindCoverageBoundary(HighlightSegment[] coverage, int position)
    {
        foreach (HighlightSegment segment in coverage)
        {
            if (position >= segment.Start && position < segment.End)
            {
                return segment.End;
            }
        }

        // Outside any recorded coverage the remainder of the input is one unstyled region.
        int lastEnd = 0;
        foreach (HighlightSegment segment in coverage)
        {
            lastEnd = Math.Max(lastEnd, segment.End);
        }

        return lastEnd <= position ? int.MaxValue : lastEnd;
    }

    private static HighlightSegment? FindCoverage(HighlightSegment[] coverage, int position)
    {
        foreach (HighlightSegment segment in coverage)
        {
            if (position >= segment.Start && position < segment.End)
            {
                return segment;
            }
        }

        return null;
    }

    private static StyleRun ResolveRun(
        AnsiStyle style,
        string text,
        byte? highlightR,
        byte? highlightG,
        byte? highlightB,
        byte? highlightBackgroundR = null,
        byte? highlightBackgroundG = null,
        byte? highlightBackgroundB = null,
        bool highlightBold = false,
        bool highlightItalic = false)
    {
        bool useAnsiForeground = style.HasForeground;
        byte? foregroundR = useAnsiForeground ? style.ForegroundR : highlightR;
        byte? foregroundG = useAnsiForeground ? style.ForegroundG : highlightG;
        byte? foregroundB = useAnsiForeground ? style.ForegroundB : highlightB;

        // An explicit ANSI background wins over a rule background; otherwise the rule color applies.
        bool useAnsiBackground = style.HasBackground;
        byte? backgroundR = useAnsiBackground ? style.BackgroundR : highlightBackgroundR;
        byte? backgroundG = useAnsiBackground ? style.BackgroundG : highlightBackgroundG;
        byte? backgroundB = useAnsiBackground ? style.BackgroundB : highlightBackgroundB;

        bool inverse = false;
        if (style.Reverse && style.HasForeground && style.HasBackground)
        {
            (foregroundR, backgroundR) = (backgroundR, foregroundR);
            (foregroundG, backgroundG) = (backgroundG, foregroundG);
            (foregroundB, backgroundB) = (backgroundB, foregroundB);
        }
        else if (style.Reverse)
        {
            inverse = true;
        }

        return new StyleRun(
            text,
            foregroundR,
            foregroundG,
            foregroundB,
            backgroundR,
            backgroundG,
            backgroundB,
            style.Bold || highlightBold,
            style.Underline,
            inverse,
            highlightItalic);
    }

    private static void MergeAdjacent(List<StyleRun> runs)
    {
        for (int index = runs.Count - 1; index > 0; index--)
        {
            StyleRun current = runs[index];
            StyleRun previous = runs[index - 1];
            if (!StyleEquals(current, previous))
            {
                continue;
            }

            runs[index - 1] = previous with { Text = previous.Text + current.Text };
            runs.RemoveAt(index);
        }
    }

    private static bool StyleEquals(in StyleRun left, in StyleRun right) =>
        left.ForegroundR == right.ForegroundR &&
        left.ForegroundG == right.ForegroundG &&
        left.ForegroundB == right.ForegroundB &&
        left.BackgroundR == right.BackgroundR &&
        left.BackgroundG == right.BackgroundG &&
        left.BackgroundB == right.BackgroundB &&
        left.Bold == right.Bold &&
        left.Underline == right.Underline &&
        left.Inverse == right.Inverse &&
        left.Italic == right.Italic;

    private readonly record struct HighlightSegment(
        int Start,
        int End,
        byte? ForegroundR,
        byte? ForegroundG,
        byte? ForegroundB,
        byte? BackgroundR,
        byte? BackgroundG,
        byte? BackgroundB,
        bool Bold,
        bool Italic)
    {
        public bool HasForeground => ForegroundR.HasValue && ForegroundG.HasValue && ForegroundB.HasValue;

        public bool HasBackground => BackgroundR.HasValue && BackgroundG.HasValue && BackgroundB.HasValue;

        public bool HasColoring => HasForeground || HasBackground;
    }
}
