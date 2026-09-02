using DuCom.Core.Diagnostics;
using Xunit;

namespace DuCom.Core.Tests.Diagnostics;

public sealed class PrivateMemoryThresholdEvaluatorTests
{
    [Fact]
    public void BelowLimitIsHealthy()
    {
        PrivateMemoryThresholdSnapshot snapshot = PrivateMemoryThresholdEvaluator.Evaluate(
            1024L * 1024L * 1024L - 1,
            thresholdMegabytes: 1024);

        Assert.Equal(1024L * 1024L * 1024L, snapshot.ThresholdBytes);
        Assert.Equal(PrivateMemoryThresholdState.BelowThreshold, snapshot.State);
        Assert.False(snapshot.IsThresholdReached);
    }

    [Theory]
    [InlineData(1024L * 1024L * 1024L)]
    [InlineData(1024L * 1024L * 1024L + 1)]
    public void LimitIsInclusiveLikeSuperComMemoryDog(long privateMemoryBytes)
    {
        PrivateMemoryThresholdSnapshot snapshot = PrivateMemoryThresholdEvaluator.Evaluate(
            privateMemoryBytes,
            thresholdMegabytes: 1024);

        Assert.Equal(PrivateMemoryThresholdState.ThresholdReached, snapshot.State);
        Assert.True(snapshot.IsThresholdReached);
    }

    [Fact]
    public void InvalidInputsAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PrivateMemoryThresholdEvaluator.Evaluate(-1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => PrivateMemoryThresholdEvaluator.Evaluate(0, 0));
        Assert.Throws<OverflowException>(() => PrivateMemoryThresholdEvaluator.Evaluate(0, long.MaxValue));
    }
}
