using System.Diagnostics;

namespace DuCom.Core.Parsing;

public readonly record struct AnsiProjection(
    string DisplayText,
    IReadOnlyList<StyleRun> Runs,
    bool IsVisible,
    bool HasRegexTimeout,
    bool ForceStandaloneLine = false);

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
    private const string GibberishWarning = "*************************检测到大量乱码，请检查波特率设置或者检查设备是否正常*************************";
    private static readonly long GibberishActivationTicks = Stopwatch.Frequency * 3 / 2;
    private readonly AnsiParser _parser = new();
    private readonly Func<long> _timestampProvider;
    private readonly Func<DateTimeOffset> _clock;
    private long _gibberishStartedTimestamp;
    private long _nextGibberishWarningTimestamp;
    private bool _gibberishActive;

    public AnsiDisplayProjector(
        Func<long>? timestampProvider = null,
        Func<DateTimeOffset>? clock = null)
    {
        _timestampProvider = timestampProvider ?? Stopwatch.GetTimestamp;
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    /// <summary>Drops all parser state; used when the display is cleared.</summary>
    public void Reset()
    {
        _parser.Reset();
        _gibberishStartedTimestamp = 0;
        _nextGibberishWarningTimestamp = 0;
        _gibberishActive = false;
    }

    public AnsiProjection Project(string segment, IReadOnlyList<HighlightFilterRule>? highlightRules)
    {
        ArgumentNullException.ThrowIfNull(segment);

        bool suspicious = DisplayTextSafety.IsSuspiciousText(segment);
        if (suspicious || _gibberishActive && !DisplayTextSafety.IsClearlyReadableText(segment))
        {
            _parser.Reset();
            long now = _timestampProvider();
            if (!_gibberishActive)
            {
                _gibberishActive = true;
                _gibberishStartedTimestamp = now;
                _nextGibberishWarningTimestamp = 0;
            }

            if (now - _gibberishStartedTimestamp < GibberishActivationTicks ||
                _nextGibberishWarningTimestamp != 0 && now < _nextGibberishWarningTimestamp)
            {
                return new AnsiProjection(string.Empty, [], false, false);
            }

            _nextGibberishWarningTimestamp = now + Stopwatch.Frequency;
            string warning = $"[{_clock():HH:mm:ss.fff}] {GibberishWarning}";
            return ProjectNeutral(warning, highlightRules) with { ForceStandaloneLine = true };
        }

        if (_gibberishActive)
        {
            _gibberishActive = false;
            _gibberishStartedTimestamp = 0;
            _nextGibberishWarningTimestamp = 0;
        }

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
