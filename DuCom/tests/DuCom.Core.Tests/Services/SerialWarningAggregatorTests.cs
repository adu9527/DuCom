using DuCom.Services;

namespace DuCom.Core.Tests.Services;

public sealed class SerialWarningAggregatorTests
{
    [Fact]
    public async Task RepeatedWarningsArePublishedAsOneCountedSummary()
    {
        TaskCompletionSource<(string Warning, long Count, bool HighMemory)> published =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        using SerialWarningAggregator aggregator = new(
            () => long.MaxValue,
            (warning, count, highMemory) => published.TrySetResult((warning, count, highMemory)));

        for (int index = 0; index < 10_000; index++)
        {
            aggregator.Report("SerialWarning.Frame");
        }

        (string warning, long count, bool highMemory) = await published.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal("SerialWarning.Frame", warning);
        Assert.Equal(10_000, count);
        Assert.False(highMemory);
    }

    [Fact]
    public void DisposeFlushesPendingCountsWithoutDroppingWarnings()
    {
        List<(string Warning, long Count)> published = [];
        SerialWarningAggregator aggregator = new(
            () => long.MaxValue,
            (warning, count, _) => published.Add((warning, count)));

        aggregator.Report("SerialWarning.Frame");
        aggregator.Report("SerialWarning.Frame");
        aggregator.Report("SerialWarning.Overrun");
        aggregator.Dispose();

        Assert.Contains(("SerialWarning.Frame", 2), published);
        Assert.Contains(("SerialWarning.Overrun", 1), published);
    }
}
