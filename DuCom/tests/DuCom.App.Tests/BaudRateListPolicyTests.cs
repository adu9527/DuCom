using DuCom.Services;
using Xunit;

namespace DuCom.App.Tests;

public sealed class BaudRateListPolicyTests
{
    [Fact]
    public void AddReturnsNullForDuplicate()
    {
        IReadOnlyList<int> list = [115_200, 9_600];
        Assert.Null(BaudRateListPolicy.Add(list, 9_600));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AddReturnsNullForNonPositiveValue(int value)
    {
        IReadOnlyList<int> list = [9_600];
        Assert.Null(BaudRateListPolicy.Add(list, value));
    }

    [Fact]
    public void AddInsertsAndKeepsAscendingOrder()
    {
        IReadOnlyList<int> list = [9_600, 115_200];
        IReadOnlyList<int>? updated = BaudRateListPolicy.Add(list, 19_200);
        Assert.NotNull(updated);
        Assert.Equal([9_600, 19_200, 115_200], updated);
    }

    [Fact]
    public void EnsurePresentAddsMissingValueSorted()
    {
        IReadOnlyList<int> updated = BaudRateListPolicy.EnsurePresent([115_200], 3_000_000);
        Assert.Equal([115_200, 3_000_000], updated);
    }

    [Fact]
    public void EnsurePresentIgnoresNonPositiveValue()
    {
        IReadOnlyList<int> list = [115_200, 9_600];
        IReadOnlyList<int> updated = BaudRateListPolicy.EnsurePresent(list, 0);
        Assert.Equal([9_600, 115_200], updated);
    }

    [Fact]
    public void EnsurePresentIsIdempotentForExistingValue()
    {
        IReadOnlyList<int> list = [9_600, 115_200];
        Assert.Equal([9_600, 115_200], BaudRateListPolicy.EnsurePresent(list, 9_600));
    }

    [Fact]
    public void PruneToDefaultsKeepsDefaultsAndInUseRates()
    {
        // 987_654 is a rate currently in use: kept alongside the defaults. A custom
        // rate that is not in use never enters the result.
        IReadOnlyList<int> result = BaudRateListPolicy.PruneToDefaults([987_654]);
        Assert.Contains(987_654, result);
        Assert.DoesNotContain(12345, result);
        Assert.All(BaudRateListPolicy.DefaultBaudRates, rate => Assert.Contains(rate, result));
        Assert.Equal(result.Order(), result);
    }

    [Fact]
    public void PruneToDefaultsIsNeverEmpty()
    {
        IReadOnlyList<int> result = BaudRateListPolicy.PruneToDefaults([]);
        Assert.NotEmpty(result);
        Assert.Equal(BaudRateListPolicy.DefaultBaudRates, result);
    }
}
