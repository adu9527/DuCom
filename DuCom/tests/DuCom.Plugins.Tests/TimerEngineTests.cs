using System.Text.Json;
using DuCom.Plugins.Timer;
using Xunit;

namespace DuCom.Plugins.Tests;

public sealed class TimerEngineTests
{
    private const long T0 = 1_700_000_000_000;

    [Fact]
    public void Start_Resets_Session()
    {
        TimerSession running = TimerEngine.Start(new TimerSession { Mode = StopwatchMode.Paused, AccumulatedMs = 12_345 }, T0);
        Assert.Equal(StopwatchMode.Running, running.Mode);
        Assert.Equal(T0, running.AnchorUnixMs);
        Assert.Equal(0, running.AccumulatedMs);
        Assert.Empty(running.Laps);
    }

    [Fact]
    public void Pause_Folds_Elapsed_Into_Accumulated()
    {
        TimerSession session = TimerEngine.Start(new TimerSession(), T0);
        TimerSession paused = TimerEngine.Pause(session, T0 + 2_500);
        Assert.Equal(StopwatchMode.Paused, paused.Mode);
        Assert.Null(paused.AnchorUnixMs);
        Assert.Equal(2_500, paused.AccumulatedMs);
        Assert.Equal(2_500, paused.ElapsedMs(T0 + 60_000));
    }

    [Fact]
    public void Resume_Keeps_Accumulated_And_Adds_New_Anchor()
    {
        TimerSession paused = new() { Mode = StopwatchMode.Paused, AccumulatedMs = 2_500 };
        TimerSession resumed = TimerEngine.Resume(paused, T0);
        Assert.Equal(StopwatchMode.Running, resumed.Mode);
        Assert.Equal(T0, resumed.AnchorUnixMs);
        Assert.Equal(2_500, resumed.AccumulatedMs);
        Assert.Equal(4_500, resumed.ElapsedMs(T0 + 2_000));
    }

    [Fact]
    public void RecordLap_Computes_Split_And_Total()
    {
        TimerSession session = TimerEngine.Start(new TimerSession(), T0);
        (session, LapRecord? first) = TimerEngine.RecordLap(session, T0 + 1_500);
        (session, LapRecord? second) = TimerEngine.RecordLap(session, T0 + 4_000);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(1, first!.Index);
        Assert.Equal(1_500, first.LapMs);
        Assert.Equal(1_500, first.TotalMs);
        Assert.Equal(2, second!.Index);
        Assert.Equal(2_500, second.LapMs);
        Assert.Equal(4_000, second.TotalMs);
    }

    [Fact]
    public void RecordLap_Returns_Null_When_Not_Running()
    {
        (TimerSession session, LapRecord? lap) = TimerEngine.RecordLap(new TimerSession(), T0);
        Assert.Null(lap);
        Assert.Empty(session.Laps);
    }

    [Fact]
    public void ComputeStats_Handles_Empty_And_Mixed_Laps()
    {
        Assert.Null(TimerEngine.ComputeStats([]));

        LapStats? stats = TimerEngine.ComputeStats(
        [
            new LapRecord(1, 1_000, 1_000, T0),
            new LapRecord(2, 3_000, 4_000, T0),
            new LapRecord(3, 2_000, 6_000, T0),
        ]);
        Assert.NotNull(stats);
        Assert.Equal(1, stats!.FastestIndex);
        Assert.Equal(1_000, stats.FastestMs);
        Assert.Equal(2, stats.SlowestIndex);
        Assert.Equal(3_000, stats.SlowestMs);
        Assert.Equal(2_000, stats.AverageMs);
    }

    [Theory]
    [InlineData(0, "00:00.0")]
    [InlineData(40, "00:00.0")]
    [InlineData(50, "00:00.1")]
    [InlineData(949, "00:00.9")]
    [InlineData(950, "00:01.0")]
    [InlineData(1_500, "00:01.5")]
    [InlineData(59_949, "00:59.9")]
    [InlineData(3_600_000, "1:00:00.0")]
    [InlineData(3_723_456, "1:02:03.5")]
    public void FormatElapsed_Rounds_To_Tenths(long milliseconds, string expected)
    {
        Assert.Equal(expected, TimerEngine.FormatElapsed(milliseconds));
    }

    [Theory]
    [InlineData(0, "-")]
    [InlineData(520, "+0.5s")]
    [InlineData(1_540, "+1.5s")]
    [InlineData(-520, "-0.5s")]
    public void FormatDelta_Rounds_To_Tenths(long deltaMs, string expected)
    {
        Assert.Equal(expected, TimerEngine.FormatDelta(deltaMs));
    }

    [Fact]
    public void BuildExportText_Includes_Laps_And_Stats()
    {
        TimerSession session = new()
        {
            Mode = StopwatchMode.Paused,
            AccumulatedMs = 4_000,
            Laps =
            [
                new LapRecord(1, 1_500, 1_500, T0),
                new LapRecord(2, 2_500, 4_000, T0 + 4_000),
            ],
        };
        string text = TimerEngine.BuildExportText(session, DateTimeOffset.FromUnixTimeMilliseconds(T0 + 9_000).ToLocalTime(), chinese: true);

        Assert.Contains("计次: 2", text);
        Assert.Contains("#1", text);
        Assert.Contains("#2", text);
        Assert.Contains("00:01.5", text);
        Assert.Contains("+1.0s", text);
        Assert.Contains("最快: #1 00:01.5", text);
        Assert.Contains("平均分段: 00:02.0", text);
    }

    [Fact]
    public void PluginState_RoundTrips_Through_Json()
    {
        TimerPluginState state = new(
            new TimerSession
            {
                Mode = StopwatchMode.Running,
                AnchorUnixMs = T0,
                AccumulatedMs = 4_000,
                Laps = [new LapRecord(1, 1_500, 1_500, T0)],
            },
            new TimerSession { Mode = StopwatchMode.Paused, AccumulatedMs = 9_000 });

        TimerPluginState? loaded = JsonSerializer.Deserialize<TimerPluginState>(
            JsonSerializer.Serialize(state, TimerEngine.JsonOptions), TimerEngine.JsonOptions);

        Assert.NotNull(loaded);
        Assert.Equal(StopwatchMode.Running, loaded!.Current!.Mode);
        Assert.Equal(T0, loaded.Current.AnchorUnixMs);
        Assert.Equal(4_000, loaded.Current.AccumulatedMs);
        Assert.Single(loaded.Current.Laps);
        Assert.Equal(9_000, loaded.Previous!.AccumulatedMs);
    }

    [Fact]
    public void Running_Session_Keeps_Counting_Across_Restart()
    {
        TimerSession persisted = TimerEngine.Pause(TimerEngine.Start(new TimerSession(), T0), T0 + 1_000);
        long elapsedMuchLater = persisted.ElapsedMs(T0 + 10_000_000);
        Assert.Equal(1_000, elapsedMuchLater);

        TimerSession running = TimerEngine.Start(new TimerSession(), T0);
        Assert.Equal(600_000, running.ElapsedMs(T0 + 600_000));
    }
}
