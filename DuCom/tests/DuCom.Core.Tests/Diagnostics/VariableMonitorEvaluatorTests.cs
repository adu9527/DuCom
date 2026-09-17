using DuCom.Core.Diagnostics;
using System.Globalization;

namespace DuCom.Core.Tests.Diagnostics;

public sealed class VariableMonitorEvaluatorTests
{
    private static VariableMonitorRule CreateRule(
        string name,
        string pattern,
        string? portName = null,
        bool enabled = true,
        int order = 0) => new(Guid.NewGuid(), name, portName, pattern, enabled, order);

    [Fact]
    public void AppendLine_UpdatesSampleWithFirstCaptureGroup()
    {
        VariableMonitorEvaluator evaluator = new();
        evaluator.UpdateRules([CreateRule("temp", @"temp=(\d+(?:\.\d+)?)")]);

        evaluator.AppendLine("COM3", "sensor temp=42.5 ok", DateTimeOffset.UtcNow);

        VariableMonitorSample sample = Assert.Single(evaluator.Samples);
        Assert.Equal("42.5", sample.Value);
        Assert.Equal(1, sample.MatchCount);
    }

    [Fact]
    public void AppendLine_RepeatedMatches_CountAndKeepLatest()
    {
        VariableMonitorEvaluator evaluator = new();
        evaluator.UpdateRules([CreateRule("temp", @"temp=(\d+)")]);

        evaluator.AppendLine(null, "temp=1", DateTimeOffset.UtcNow);
        evaluator.AppendLine(null, "temp=2", DateTimeOffset.UtcNow);

        VariableMonitorSample sample = Assert.Single(evaluator.Samples);
        Assert.Equal("2", sample.Value);
        Assert.Equal(2, sample.MatchCount);
    }

    [Fact]
    public void AppendLine_WithoutCaptureGroup_UsesWholeMatch()
    {
        VariableMonitorEvaluator evaluator = new();
        evaluator.UpdateRules([CreateRule("flag", @"READY")]);

        evaluator.AppendLine(null, "device READY", DateTimeOffset.UtcNow);

        Assert.Equal("READY", Assert.Single(evaluator.Samples).Value);
    }

    [Fact]
    public void PortFilter_OnlyMatchesBoundPort()
    {
        VariableMonitorEvaluator evaluator = new();
        evaluator.UpdateRules([CreateRule("temp", @"temp=(\d+)", portName: "COM3")]);

        evaluator.AppendLine("COM5", "temp=99", DateTimeOffset.UtcNow);
        Assert.Empty(evaluator.Samples);

        evaluator.AppendLine("com3", "temp=7", DateTimeOffset.UtcNow);
        Assert.Equal("7", Assert.Single(evaluator.Samples).Value);
    }

    [Fact]
    public void DisabledRule_IsIgnored()
    {
        VariableMonitorEvaluator evaluator = new();
        evaluator.UpdateRules([CreateRule("temp", @"temp=(\d+)", enabled: false)]);

        evaluator.AppendLine(null, "temp=1", DateTimeOffset.UtcNow);

        Assert.Empty(evaluator.Samples);
    }

    [Fact]
    public void Samples_AreOrderedByRuleOrder()
    {
        VariableMonitorEvaluator evaluator = new();
        evaluator.UpdateRules([
            CreateRule("second", @"b=(\d+)", order: 2),
            CreateRule("first", @"a=(\d+)", order: 1),
        ]);

        evaluator.AppendLine(null, "a=1 b=2", DateTimeOffset.UtcNow);

        Assert.Equal(["1", "2"], evaluator.Samples.Select(sample => sample.Value));
    }

    [Fact]
    public void CatastrophicRegex_TimesOutWithoutThrowing()
    {
        VariableMonitorEvaluator evaluator = new();
        evaluator.UpdateRules([CreateRule("slow", @"(?<x>a+)+b")]);

        evaluator.AppendLine(null, new string('a', 40) + "c", DateTimeOffset.UtcNow);

        Assert.True(evaluator.HasRegexTimedOut);
        Assert.Empty(evaluator.Samples);
    }

    [Fact]
    public void InvalidRegex_IsIgnored()
    {
        VariableMonitorEvaluator evaluator = new();
        evaluator.UpdateRules([CreateRule("bad", "[invalid")]);

        evaluator.AppendLine(null, "[invalid", DateTimeOffset.UtcNow);

        Assert.Empty(evaluator.Samples);
    }

    [Fact]
    public void UpdateRules_DropsSamplesOfRemovedRules()
    {
        VariableMonitorRule rule = CreateRule("temp", @"temp=(\d+)");
        VariableMonitorEvaluator evaluator = new();
        evaluator.UpdateRules([rule]);
        evaluator.AppendLine(null, "temp=1", DateTimeOffset.UtcNow);

        evaluator.UpdateRules([CreateRule("other", @"x=(\d+)")]);

        Assert.Empty(evaluator.Samples);
    }

    [Fact]
    public void Rule_SixArgumentConstructionUsesPlottingDefaults()
    {
        VariableMonitorRule rule = CreateRule("temperature", @"(-?\d+)");

        Assert.Equal(VariableMonitorValueType.Number, rule.ValueType);
        Assert.Null(rule.ValueGroup);
        Assert.Equal(1, rule.Scale);
        Assert.Equal(0, rule.Offset);
        Assert.True(rule.PlotEnabled);
        Assert.Equal(VariableMonitorSamplingMode.EveryMatch, rule.SamplingMode);
        Assert.Equal(20, rule.SampleIntervalMs);
    }

    [Fact]
    public void AppendLine_UsesNamedGroupThenFallsBackToFirstCapture()
    {
        VariableMonitorRule named = CreateRule("named", @"x=(?<value>-?\d+(?:\.\d+)?)") with { ValueGroup = "value" };
        VariableMonitorRule fallback = CreateRule("fallback", @"y=(-?\d+)") with { ValueGroup = "missing" };
        VariableMonitorEvaluator evaluator = new();
        evaluator.UpdateRules([named, fallback]);

        IReadOnlyList<VariableNumericSample> samples = evaluator.AppendLine("COM1", "x=-1.25 y=7", DateTimeOffset.UtcNow);

        Assert.Equal([-1.25, 7], samples.Select(sample => sample.NumericValue));
        Assert.Equal(["-1.25", "7"], evaluator.Samples.Select(sample => sample.Value));
    }

    [Fact]
    public void NumericParsing_IsInvariantAndAppliesScaleAndOffset()
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            VariableMonitorRule rule = CreateRule("value", @"v=([^ ]+)") with { Scale = 2, Offset = -1 };
            VariableMonitorEvaluator evaluator = new();
            evaluator.UpdateRules([rule]);

            VariableNumericSample sample = Assert.Single(evaluator.AppendLine(null, "v=-1.25e2", DateTimeOffset.UtcNow));

            Assert.Equal(-251, sample.NumericValue);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void NumericFailuresAndNonFiniteValuesAreCountedPerRule()
    {
        VariableMonitorRule rule = CreateRule("value", @"v=(\S+)");
        VariableMonitorEvaluator evaluator = new();
        evaluator.UpdateRules([rule]);

        Assert.Empty(evaluator.AppendLine(null, "v=oops", DateTimeOffset.UtcNow));
        Assert.Empty(evaluator.AppendLine(null, "v=NaN", DateTimeOffset.UtcNow));

        VariableMonitorRuleStatus status = Assert.Single(evaluator.RuleStatuses);
        Assert.Equal(2, status.MatchCount);
        Assert.Equal(1, status.NumericParseFailureCount);
        Assert.Equal(1, status.NonFiniteValueCount);
        Assert.Equal(VariableMonitorRuleState.NonFiniteValue, status.State);
    }

    [Fact]
    public void InvalidRegexHasPerRuleStatus()
    {
        VariableMonitorEvaluator evaluator = new();
        evaluator.UpdateRules([CreateRule("bad", "[invalid")]);

        VariableMonitorRuleStatus status = Assert.Single(evaluator.RuleStatuses);
        Assert.Equal(VariableMonitorRuleState.InvalidRegex, status.State);
    }
}
