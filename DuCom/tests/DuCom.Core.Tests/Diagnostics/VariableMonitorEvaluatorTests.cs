using DuCom.Core.Diagnostics;

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
}
