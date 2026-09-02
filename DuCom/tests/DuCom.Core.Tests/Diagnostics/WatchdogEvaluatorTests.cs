using DuCom.Core.Diagnostics;

namespace DuCom.Core.Tests.Diagnostics;

public sealed class WatchdogEvaluatorTests
{
    private static WatchdogRule CreateRule(
        string pattern,
        int expectWithinSeconds = 10,
        int throttleSeconds = 60,
        WatchdogMatchMode mode = WatchdogMatchMode.Contains,
        bool isCaseSensitive = false,
        bool enabled = true) => new(
            Guid.NewGuid(),
            "rule",
            pattern,
            mode,
            isCaseSensitive,
            enabled,
            expectWithinSeconds,
            throttleSeconds,
            WatchdogActionKind.Hint,
            string.Empty);

    [Fact]
    public void Check_FiresWhenPatternNotSeenWithinWindow()
    {
        WatchdogEvaluator evaluator = new();
        var start = DateTimeOffset.UtcNow;
        evaluator.UpdateRules([CreateRule("heartbeat", expectWithinSeconds: 10)]);
        evaluator.Start(start);

        IReadOnlyList<WatchdogFiredRule> fired = evaluator.Check(start + TimeSpan.FromSeconds(11));

        var result = Assert.Single(fired);
        Assert.Contains("heartbeat", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Check_DoesNotFireWithinWindow()
    {
        WatchdogEvaluator evaluator = new();
        var start = DateTimeOffset.UtcNow;
        evaluator.UpdateRules([CreateRule("heartbeat", expectWithinSeconds: 10)]);
        evaluator.Start(start);

        Assert.Empty(evaluator.Check(start + TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void AppendLine_ResetsExpectationWindow()
    {
        WatchdogEvaluator evaluator = new();
        var start = DateTimeOffset.UtcNow;
        evaluator.UpdateRules([CreateRule("heartbeat", expectWithinSeconds: 10)]);
        evaluator.Start(start);

        evaluator.AppendLine("heartbeat ok", start + TimeSpan.FromSeconds(8));

        Assert.Empty(evaluator.Check(start + TimeSpan.FromSeconds(15)));
        Assert.Single(evaluator.Check(start + TimeSpan.FromSeconds(19)));
    }

    [Fact]
    public void Check_HonorsThrottle()
    {
        WatchdogEvaluator evaluator = new();
        var start = DateTimeOffset.UtcNow;
        evaluator.UpdateRules([CreateRule("heartbeat", expectWithinSeconds: 10, throttleSeconds: 60)]);
        evaluator.Start(start);

        Assert.Single(evaluator.Check(start + TimeSpan.FromSeconds(11)));
        Assert.Empty(evaluator.Check(start + TimeSpan.FromSeconds(12)));
        Assert.Single(evaluator.Check(start + TimeSpan.FromSeconds(80)));
    }

    [Fact]
    public void DisabledRulesNeverFire()
    {
        WatchdogEvaluator evaluator = new();
        var start = DateTimeOffset.UtcNow;
        evaluator.UpdateRules([CreateRule("heartbeat", enabled: false)]);
        evaluator.Start(start);

        Assert.Empty(evaluator.Check(start + TimeSpan.FromSeconds(100)));
    }

    [Fact]
    public void RegexRule_MatchesCaseInsensitivelyByDefault()
    {
        WatchdogEvaluator evaluator = new();
        var start = DateTimeOffset.UtcNow;
        evaluator.UpdateRules([CreateRule(@"beat \d+", expectWithinSeconds: 10, mode: WatchdogMatchMode.Regex)]);
        evaluator.Start(start);

        evaluator.AppendLine("BEAT 42", start + TimeSpan.FromSeconds(5));

        Assert.Empty(evaluator.Check(start + TimeSpan.FromSeconds(14)));
    }

    [Fact]
    public void CatastrophicRegex_TimesOutWithoutThrowingAndDoesNotResetWindow()
    {
        WatchdogEvaluator evaluator = new();
        var start = DateTimeOffset.UtcNow;
        evaluator.UpdateRules([CreateRule("(?<x>a+)+b", expectWithinSeconds: 10, mode: WatchdogMatchMode.Regex)]);
        evaluator.Start(start);

        evaluator.AppendLine(new string('a', 40) + "c", start + TimeSpan.FromSeconds(5));

        Assert.True(evaluator.HasRegexTimedOut);
        Assert.Single(evaluator.Check(start + TimeSpan.FromSeconds(16)));
    }

    [Fact]
    public void InvalidRegex_IsTreatedAsNoMatch()
    {
        WatchdogEvaluator evaluator = new();
        var start = DateTimeOffset.UtcNow;
        evaluator.UpdateRules([CreateRule("[invalid", expectWithinSeconds: 10, mode: WatchdogMatchMode.Regex)]);
        evaluator.Start(start);

        evaluator.AppendLine("[invalid", start + TimeSpan.FromSeconds(1));

        // The invalid pattern never matches, so the expectation window expires and fires.
        Assert.Single(evaluator.Check(start + TimeSpan.FromSeconds(12)));
    }

    [Fact]
    public void UpdateRules_PreservesHistoryForSurvivingRules()
    {
        WatchdogRule rule = CreateRule("heartbeat", expectWithinSeconds: 10);
        WatchdogEvaluator evaluator = new();
        var start = DateTimeOffset.UtcNow;
        evaluator.UpdateRules([rule]);
        evaluator.Start(start);
        evaluator.AppendLine("heartbeat", start + TimeSpan.FromSeconds(3));

        evaluator.UpdateRules([rule with { ExpectWithinSeconds = 20 }]);

        Assert.Empty(evaluator.Check(start + TimeSpan.FromSeconds(15)));
        Assert.Single(evaluator.Check(start + TimeSpan.FromSeconds(24)));
    }

    [Fact]
    public void UpdateRules_DropsHistoryOfRemovedRules()
    {
        WatchdogRule first = CreateRule("heartbeat", expectWithinSeconds: 10);
        WatchdogEvaluator evaluator = new();
        var start = DateTimeOffset.UtcNow;
        evaluator.UpdateRules([first]);
        evaluator.Start(start);
        evaluator.AppendLine("heartbeat", start + TimeSpan.FromSeconds(3));

        WatchdogRule replacement = CreateRule("heartbeat", expectWithinSeconds: 10);
        evaluator.UpdateRules([replacement]);
        evaluator.Start(start);

        // Replacement has no history: its anchor is the new session start.
        Assert.Empty(evaluator.Check(start + TimeSpan.FromSeconds(5)));
        Assert.Single(evaluator.Check(start + TimeSpan.FromSeconds(11)));
    }

    [Fact]
    public void EmptyPattern_NeverResetsWindow()
    {
        WatchdogEvaluator evaluator = new();
        var start = DateTimeOffset.UtcNow;
        evaluator.UpdateRules([CreateRule("", expectWithinSeconds: 10)]);
        evaluator.Start(start);

        evaluator.AppendLine("anything", start + TimeSpan.FromSeconds(1));

        Assert.Single(evaluator.Check(start + TimeSpan.FromSeconds(11)));
    }
}
