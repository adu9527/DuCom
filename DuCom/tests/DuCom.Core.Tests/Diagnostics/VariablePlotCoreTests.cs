using System.Globalization;
using DuCom.Core.Diagnostics;
using DuCom.Core.Storage;

namespace DuCom.Core.Tests.Diagnostics;

public sealed class VariableSeriesBufferTests
{
    private static readonly Guid RuleId = Guid.NewGuid();

    [Fact]
    public void BufferEvictsByCapacityAndRetentionWithoutMutatingSnapshot()
    {
        VariableSeriesBuffer buffer = new(3, TimeSpan.FromSeconds(2));
        DateTimeOffset start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        buffer.Add(Sample(start, 1));
        buffer.Add(Sample(start.AddSeconds(1), 2));
        IReadOnlyList<VariableNumericSample> oldSnapshot = buffer.Snapshot();
        buffer.Add(Sample(start.AddSeconds(3), 3));
        buffer.Add(Sample(start.AddSeconds(4), 4));
        buffer.Add(Sample(start.AddSeconds(5), 5));

        Assert.Equal([1d, 2d], oldSnapshot.Select(sample => sample.NumericValue));
        Assert.Equal([3d, 4d, 5d], buffer.Snapshot().Select(sample => sample.NumericValue));
        Assert.Equal(2, buffer.EvictedSampleCount);
    }

    private static VariableNumericSample Sample(DateTimeOffset timestamp, double value) =>
        new(RuleId, "COM1", timestamp, value, (long)value);
}

public sealed class VariablePlotDownsamplerTests
{
    [Fact]
    public void MinMaxPreservesEndpointsAndSpikesWithinBudget()
    {
        Guid id = Guid.NewGuid();
        DateTimeOffset start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        VariableNumericSample[] input = [.. Enumerable.Range(0, 100).Select(index =>
            new VariableNumericSample(id, null, start.AddMilliseconds(index), index == 40 ? 1_000 : index == 60 ? -900 : 0, index))];

        IReadOnlyList<VariableNumericSample> result = VariablePlotDownsampler.MinMax(input, 12);

        Assert.True(result.Count <= 12);
        Assert.Same(input[0], result[0]);
        Assert.Same(input[^1], result[^1]);
        Assert.Contains(result, sample => sample.NumericValue == 1_000);
        Assert.Contains(result, sample => sample.NumericValue == -900);
        Assert.True(result.Zip(result.Skip(1)).All(pair => pair.First.SampledAtUtc <= pair.Second.SampledAtUtc));
    }
}

public sealed class VariablePlotCsvTests
{
    [Fact]
    public void LongTableUsesInvariantRoundTripValuesAndEscapesFields()
    {
        Guid id = Guid.NewGuid();
        DateTimeOffset timestamp = new(2026, 9, 13, 10, 0, 0, 123, TimeSpan.Zero);
        VariableMonitorRule rule = new(id, "Gyro, \"X\"", "COM3", @"x=(\d+)", true, 0) { Unit = "dps" };
        VariablePlotSnapshot snapshot = new(
            2,
            [new VariableSeriesSnapshot(rule, [new VariableNumericSample(id, "COM3", timestamp, 12.5, 1042)], 0)],
            [],
            []);
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            string csv = VariablePlotCsv.ToLongTable(snapshot);

            Assert.StartsWith("TimestampUtc,TimestampLocal,RuleId,Series,Port,Value,Unit,MatchCount\r\n", csv);
            Assert.Contains(timestamp.ToString("O", CultureInfo.InvariantCulture), csv);
            Assert.Contains(",\"Gyro, \"\"X\"\"\",COM3,12.5,dps,1042\r\n", csv);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}

public sealed class VariablePlotEngineTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TickUsesSourceTimestampsForAverageBucketsAndPullsMultiplePages()
    {
        Guid id = Guid.NewGuid();
        VariableMonitorRule rule = new(id, "value", "COM1", @"v=(\d+)", true, 0)
        {
            SamplingMode = VariableMonitorSamplingMode.Average,
            SampleIntervalMs = 100,
        };
        VariableMonitorEngine engine = new(new VariableMonitorEngineOptions(MaximumPagesPerTick: 8));
        engine.UpdateRules([rule]);
        Queue<LineStoreSnapshot> pages = new([
            Snapshot(),
            Snapshot(Line(1, 0, 10), Line(2, 50, 20)),
            Snapshot(Line(3, 110, 30)),
            Snapshot(),
        ]);
        VariableMonitorSessionProbe probe = new("COM1", true, _ => pages.Dequeue());

        engine.Tick([probe]);

        VariableSeriesSnapshot series = Assert.Single(engine.GetPlotSnapshot().Series);
        VariableNumericSample sample = Assert.Single(series.Samples);
        Assert.Equal(15, sample.NumericValue);
        Assert.Equal(2, sample.SourceSampleCount);
    }

    [Fact]
    public void TickHonorsItemBudgetAndContinuesFromCursor()
    {
        Guid id = Guid.NewGuid();
        VariableMonitorEngine engine = new(new VariableMonitorEngineOptions(MaximumItemsPerTick: 2));
        engine.UpdateRules([new VariableMonitorRule(id, "value", null, @"v=(\d+)", true, 0)]);
        List<StoredLine> lines = [Line(1, 0, 1), Line(2, 1, 2), Line(3, 2, 3), Line(4, 3, 4)];
        int pulls = 0;
        VariableMonitorSessionProbe probe = new("COM1", true, cursor =>
            Interlocked.Increment(ref pulls) == 1
                ? Snapshot()
                : Snapshot(lines.Where(line => cursor is null || line.LogicalId > cursor.Value.LogicalId).ToArray()));

        engine.Tick([probe]);
        Assert.Equal([1d, 2d], Assert.Single(engine.GetPlotSnapshot().Series).Samples.Select(sample => sample.NumericValue));

        engine.Tick([probe]);
        Assert.Equal([1d, 2d, 3d, 4d], Assert.Single(engine.GetPlotSnapshot().Series).Samples.Select(sample => sample.NumericValue));
    }

    [Fact]
    public void RuntimeChangeReanchorsAndStaleCursorRecordsGap()
    {
        Guid id = Guid.NewGuid();
        VariableMonitorEngine engine = new();
        engine.UpdateRules([new VariableMonitorRule(id, "value", null, @"v=(\d+)", true, 0)]);
        Queue<LineStoreSnapshot> firstRuntime = new([
            new LineStoreSnapshot(1, 1, 0, []),
            new LineStoreSnapshot(5, 6, 3, [Line(5, 0, 5), Line(6, 1, 6)]),
            Snapshot(),
        ]);
        VariableMonitorSessionProbe first = new("COM1", true, _ => firstRuntime.Dequeue()) { RuntimeId = "runtime-1" };

        engine.Tick([first]);
        Assert.Single(engine.GetPlotSnapshot().Gaps);

        int pulls = 0;
        VariableMonitorSessionProbe second = new("COM1", true, _ =>
            Interlocked.Increment(ref pulls) == 1 ? new LineStoreSnapshot(100, 100, 0, [Line(100, 0, 100)]) : Snapshot())
        { RuntimeId = "runtime-2" };
        engine.Tick([second]);

        Assert.Equal(2, pulls);
        Assert.Equal([5d, 6d], Assert.Single(engine.GetPlotSnapshot().Series).Samples.Select(sample => sample.NumericValue));
    }

    [Fact]
    public void CosmeticRuleChangesRetainHistorySemanticChangesClearAndAdvanceGeneration()
    {
        Guid id = Guid.NewGuid();
        VariableMonitorRule rule = new(id, "old", null, @"v=(\d+)", true, 0);
        VariableMonitorEngine engine = new();
        engine.UpdateRules([rule]);
        Queue<LineStoreSnapshot> pages = new([Snapshot(), Snapshot(Line(1, 0, 7)), Snapshot()]);
        engine.Tick([new VariableMonitorSessionProbe("COM1", true, _ => pages.Dequeue())]);
        long generation = engine.GetPlotSnapshot().Generation;

        engine.UpdateRules([rule with { Name = "new", Color = "#fff" }]);
        Assert.Single(Assert.Single(engine.GetPlotSnapshot().Series).Samples);
        Assert.Equal(generation, engine.GetPlotSnapshot().Generation);

        engine.UpdateRules([rule with { Scale = 2 }]);
        VariablePlotSnapshot cleared = engine.GetPlotSnapshot();
        Assert.Empty(Assert.Single(cleared.Series).Samples);
        Assert.True(cleared.Generation > generation);
    }

    [Fact]
    public void ClearHistoryAdvancesGenerationAndSnapshotsAreIndependent()
    {
        Guid id = Guid.NewGuid();
        VariableMonitorEngine engine = new();
        engine.UpdateRules([new VariableMonitorRule(id, "value", null, @"v=(\d+)", true, 0)]);
        Queue<LineStoreSnapshot> pages = new([Snapshot(), Snapshot(Line(1, 0, 7)), Snapshot()]);
        engine.Tick([new VariableMonitorSessionProbe("COM1", true, _ => pages.Dequeue())]);
        VariablePlotSnapshot before = engine.GetPlotSnapshot();

        engine.ClearHistory();
        VariablePlotSnapshot after = engine.GetPlotSnapshot();

        Assert.Single(Assert.Single(before.Series).Samples);
        Assert.Empty(Assert.Single(after.Series).Samples);
        Assert.Null(Assert.Single(engine.GetRuleStates()).Sample);
        Assert.Equal(0, Assert.Single(after.RuleStatuses).MatchCount);
        Assert.Equal(before.Generation + 1, after.Generation);
    }

    private static StoredLine Line(long id, int milliseconds, int value) =>
        new(id, 0, LineDirection.Rx, Start.AddMilliseconds(milliseconds), $"v={value}", true);

    private static LineStoreSnapshot Snapshot(params StoredLine[] lines) => new(
        lines.Length == 0 ? null : lines[0].LogicalId,
        lines.Length == 0 ? null : lines[^1].LogicalId,
        0,
        lines);
}
