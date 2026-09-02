namespace DuCom.Core.Parsing;

public readonly record struct AnsiProjection(
    string DisplayText,
    IReadOnlyList<StyleRun> Runs,
    bool IsVisible,
    bool HasRegexTimeout);

/// <summary>
/// Per-session projector turning raw display segments into clean text plus styled runs.
/// The <see cref="AnsiParser"/> instance is persistent, so escape sequences and active styles
/// split across soft-wrapped segments (or receive blocks) continue to resolve correctly.
/// Plain segments take a fast path only while the parser sits at a neutral state; otherwise
/// they flow through the parser so carried-over styling is preserved. HEX-formatted text
/// contains no ESC characters at all, so ANSI interpretation never triggers for it.
/// </summary>
public sealed class AnsiDisplayProjector
{
    private readonly AnsiParser _parser = new();

    /// <summary>Drops all parser state; used when the display is cleared.</summary>
    public void Reset() => _parser.Reset();

    public AnsiProjection Project(string segment, IReadOnlyList<HighlightFilterRule>? highlightRules)
    {
        ArgumentNullException.ThrowIfNull(segment);

        if (_parser.IsAtNeutralPlainState && !segment.Contains('\u001B'))
        {
            return ProjectNeutral(segment, highlightRules);
        }

        IReadOnlyList<AnsiRun> ansiRuns = _parser.Parse(segment);
        System.Text.StringBuilder clean = new(ansiRuns.Sum(run => run.Text.Length));
        foreach (AnsiRun run in ansiRuns)
        {
            clean.Append(run.Text);
        }

        string displayText = clean.ToString();
        HighlightFilterEvaluation evaluation = HighlightFilterRuleMatcher.Evaluate(highlightRules, displayText);
        IReadOnlyList<StyleRun> runs = evaluation.IsVisible
            ? StyledTextComposer.Compose(ansiRuns, evaluation.HighlightRuns)
            : [];
        return new AnsiProjection(displayText, runs, evaluation.IsVisible, evaluation.HasRegexTimeout);
    }

    private static AnsiProjection ProjectNeutral(string segment, IReadOnlyList<HighlightFilterRule>? highlightRules)
    {
        AnsiRun[] ansiRuns =
        [
            new(segment, AnsiStyle.Default),
        ];
        HighlightFilterEvaluation evaluation = HighlightFilterRuleMatcher.Evaluate(highlightRules, segment);
        IReadOnlyList<StyleRun> runs = evaluation.IsVisible
            ? StyledTextComposer.Compose(ansiRuns, evaluation.HighlightRuns)
            : [];
        return new AnsiProjection(segment, runs, evaluation.IsVisible, evaluation.HasRegexTimeout);
    }
}
