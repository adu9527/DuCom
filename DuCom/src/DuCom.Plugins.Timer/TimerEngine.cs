using System.Globalization;
using System.Text;
using System.Text.Json;

namespace DuCom.Plugins.Timer;

public enum StopwatchMode
{
    Idle,
    Running,
    Paused,
}

public sealed record LapRecord(int Index, long LapMs, long TotalMs, long WallClockUnixMs);

public sealed record TimerSession
{
    public StopwatchMode Mode { get; init; } = StopwatchMode.Idle;

    public long? AnchorUnixMs { get; init; }

    public long AccumulatedMs { get; init; }

    public IReadOnlyList<LapRecord> Laps { get; init; } = [];

    public long ElapsedMs(long nowUnixMs) => Mode switch
    {
        StopwatchMode.Running when AnchorUnixMs is long anchor => AccumulatedMs + Math.Max(0, nowUnixMs - anchor),
        StopwatchMode.Paused => AccumulatedMs,
        _ => 0,
    };
}

public sealed record LapStats(int FastestIndex, long FastestMs, int SlowestIndex, long SlowestMs, long AverageMs);

public sealed record TimerPluginState(TimerSession? Current = null, TimerSession? Previous = null);

public static class TimerEngine
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static TimerSession Start(TimerSession session, long nowUnixMs) => new()
    {
        Mode = StopwatchMode.Running,
        AnchorUnixMs = nowUnixMs,
    };

    public static TimerSession Pause(TimerSession session, long nowUnixMs) => session with
    {
        Mode = StopwatchMode.Paused,
        AccumulatedMs = session.ElapsedMs(nowUnixMs),
        AnchorUnixMs = null,
    };

    public static TimerSession Resume(TimerSession session, long nowUnixMs) => session.Mode == StopwatchMode.Paused
        ? session with { Mode = StopwatchMode.Running, AnchorUnixMs = nowUnixMs }
        : session;

    public static (TimerSession Session, LapRecord? Lap) RecordLap(TimerSession session, long nowUnixMs)
    {
        if (session.Mode != StopwatchMode.Running)
        {
            return (session, null);
        }

        long total = session.ElapsedMs(nowUnixMs);
        long previousTotal = session.Laps.Count == 0 ? 0 : session.Laps[^1].TotalMs;
        LapRecord lap = new(session.Laps.Count + 1, Math.Max(0, total - previousTotal), total, nowUnixMs);
        return (session with { Laps = [.. session.Laps, lap] }, lap);
    }

    public static LapStats? ComputeStats(IReadOnlyList<LapRecord> laps)
    {
        if (laps.Count == 0)
        {
            return null;
        }

        LapRecord fastest = laps[0];
        LapRecord slowest = laps[0];
        long sum = 0;
        foreach (LapRecord lap in laps)
        {
            if (lap.LapMs < fastest.LapMs)
            {
                fastest = lap;
            }

            if (lap.LapMs > slowest.LapMs)
            {
                slowest = lap;
            }

            sum += lap.LapMs;
        }

        return new LapStats(fastest.Index, fastest.LapMs, slowest.Index, slowest.LapMs, (long)Math.Round(sum / (double)laps.Count, MidpointRounding.AwayFromZero));
    }

    public static string FormatElapsed(long milliseconds)
    {
        long clamped = Math.Max(0, milliseconds);
        long tenths = (clamped + 50) / 100;
        long tenth = tenths % 10;
        long totalSeconds = tenths / 10;
        long seconds = totalSeconds % 60;
        long minutes = (totalSeconds / 60) % 60;
        long hours = totalSeconds / 3600;
        return hours > 0
            ? $"{hours}:{minutes:D2}:{seconds:D2}.{tenth}"
            : $"{minutes:D2}:{seconds:D2}.{tenth}";
    }

    public static string FormatDelta(long deltaMs)
    {
        if (deltaMs == 0)
        {
            return "-";
        }

        long tenths = (Math.Abs(deltaMs) + 50) / 100;
        return $"{(deltaMs > 0 ? "+" : "-")}{tenths / 10}.{tenths % 10}s";
    }

    public static string BuildExportText(TimerSession session, DateTimeOffset exportedAt, bool chinese)
    {
        StringBuilder builder = new();
        LapStats? stats = ComputeStats(session.Laps);
        long totalMs = session.Mode == StopwatchMode.Running && session.Laps.Count > 0
            ? session.Laps[^1].TotalMs
            : session.ElapsedMs(exportedAt.ToUnixTimeMilliseconds());
        builder.AppendLine(chinese ? "DuCom 秒表计次记录" : "DuCom stopwatch lap records");
        builder.AppendLine(string.Create(CultureInfo.InvariantCulture, $"{(chinese ? "导出时间" : "Exported")}: {exportedAt:yyyy-MM-dd HH:mm:ss}"));
        builder.AppendLine(string.Create(CultureInfo.InvariantCulture, $"{(chinese ? "计次" : "Laps")}: {session.Laps.Count}    {(chinese ? "总时长" : "Total")}: {FormatElapsed(totalMs)}"));
        if (stats is { } value)
        {
            builder.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"{(chinese ? "最快" : "Fastest")}: #{value.FastestIndex} {FormatElapsed(value.FastestMs)}    {(chinese ? "最慢" : "Slowest")}: #{value.SlowestIndex} {FormatElapsed(value.SlowestMs)}    {(chinese ? "平均分段" : "Average lap")}: {FormatElapsed(value.AverageMs)}"));
        }

        builder.AppendLine();
        builder.AppendLine(chinese ? "  #          分段          累计        与上一圈    计次时刻" : "  #          Lap           Total       Delta       At");
        long previousLapMs = 0;
        foreach (LapRecord lap in session.Laps)
        {
            long delta = lap.LapMs - previousLapMs;
            previousLapMs = lap.LapMs;
            DateTimeOffset wallClock = DateTimeOffset.FromUnixTimeMilliseconds(lap.WallClockUnixMs).ToLocalTime();
            builder.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"{lap.Index,3}  {FormatElapsed(lap.LapMs),12}  {FormatElapsed(lap.TotalMs),12}  {FormatDelta(delta),10}  {wallClock:HH:mm:ss}"));
        }

        return builder.ToString();
    }
}
