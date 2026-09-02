namespace DuCom.Core.Diagnostics;

public enum PrivateMemoryThresholdState
{
    BelowThreshold,
    ThresholdReached,
}

/// <summary>
/// Immutable result of comparing process private memory with a configured limit. This is
/// the MemoryDog-compatible memory contract; it is intentionally separate from the
/// content/rule watchdog.
/// </summary>
public sealed record PrivateMemoryThresholdSnapshot(
    long PrivateMemoryBytes,
    long ThresholdBytes,
    PrivateMemoryThresholdState State)
{
    public bool IsThresholdReached => State == PrivateMemoryThresholdState.ThresholdReached;
}

public static class PrivateMemoryThresholdEvaluator
{
    public const long BytesPerMegabyte = 1024L * 1024L;

    public static PrivateMemoryThresholdSnapshot Evaluate(long privateMemoryBytes, long thresholdMegabytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(privateMemoryBytes);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(thresholdMegabytes, 0);

        long thresholdBytes = checked(thresholdMegabytes * BytesPerMegabyte);
        PrivateMemoryThresholdState state = privateMemoryBytes >= thresholdBytes
            ? PrivateMemoryThresholdState.ThresholdReached
            : PrivateMemoryThresholdState.BelowThreshold;
        return new PrivateMemoryThresholdSnapshot(privateMemoryBytes, thresholdBytes, state);
    }
}
