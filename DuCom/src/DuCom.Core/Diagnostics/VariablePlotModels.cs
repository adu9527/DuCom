using System.Collections.ObjectModel;

namespace DuCom.Core.Diagnostics;

public sealed record VariableNumericSample(
    Guid RuleId,
    string? PortName,
    DateTimeOffset SampledAtUtc,
    double NumericValue,
    long MatchCount,
    int SourceSampleCount = 1);

public sealed record VariableMonitorGap(
    string PortName,
    DateTimeOffset DetectedAtUtc,
    long EstimatedLostLineCount,
    string Reason);

public sealed record VariableSeriesSnapshot(
    VariableMonitorRule Rule,
    IReadOnlyList<VariableNumericSample> Samples,
    long EvictedSampleCount);

public sealed record VariablePlotSnapshot(
    long Generation,
    IReadOnlyList<VariableSeriesSnapshot> Series,
    IReadOnlyList<VariableMonitorGap> Gaps,
    IReadOnlyList<VariableMonitorRuleStatus> RuleStatuses);

public sealed record VariableMonitorEngineOptions(
    int MaximumPointsPerSeries = 100_000,
    TimeSpan? Retention = null,
    int MaximumPagesPerTick = 8,
    int MaximumItemsPerTick = 16_384,
    TimeSpan? MaximumWorkTimePerTick = null)
{
    public TimeSpan EffectiveRetention => Retention ?? TimeSpan.FromMinutes(5);
    public TimeSpan EffectiveMaximumWorkTimePerTick => MaximumWorkTimePerTick ?? TimeSpan.FromMilliseconds(50);
}

internal sealed class VariableSeriesBuffer
{
    private readonly VariableNumericSample?[] _items;
    private readonly TimeSpan _retention;
    private int _start;
    private int _count;

    public VariableSeriesBuffer(int capacity, TimeSpan retention)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _items = new VariableNumericSample[capacity];
        _retention = retention < TimeSpan.Zero ? TimeSpan.Zero : retention;
    }

    public long EvictedSampleCount { get; private set; }

    public void Add(VariableNumericSample sample)
    {
        EvictBefore(sample.SampledAtUtc - _retention);
        if (_count == _items.Length)
        {
            RemoveOldest();
        }

        _items[(_start + _count) % _items.Length] = sample;
        _count++;
    }

    public IReadOnlyList<VariableNumericSample> Snapshot()
    {
        VariableNumericSample[] result = new VariableNumericSample[_count];
        for (int index = 0; index < _count; index++)
        {
            result[index] = _items[(_start + index) % _items.Length]!;
        }

        return new ReadOnlyCollection<VariableNumericSample>(result);
    }

    private void EvictBefore(DateTimeOffset cutoff)
    {
        while (_count > 0 && _items[_start]!.SampledAtUtc < cutoff)
        {
            RemoveOldest();
        }
    }

    private void RemoveOldest()
    {
        _items[_start] = null;
        _start = (_start + 1) % _items.Length;
        _count--;
        EvictedSampleCount++;
    }
}
