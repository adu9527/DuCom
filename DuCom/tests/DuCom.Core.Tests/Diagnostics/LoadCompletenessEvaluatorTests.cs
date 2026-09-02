using DuCom.Core.Diagnostics;
using Xunit;

namespace DuCom.Core.Tests.Diagnostics;

public class LoadCompletenessEvaluatorTests
{
    [Fact]
    public void MatchingCountsProduceCompleteResult()
    {
        PipelineMetricsSnapshot pipeline = new(
            10, 1_000, 10, 1_000, 10,
            10, 1_200, 20, 0, 3, 4, 0,
            ShutdownDrainState.Completed, TimeSpan.FromMilliseconds(5));

        LoadCompletenessInfo info = LoadCompletenessEvaluator.Evaluate(pipeline, generatorInputBlocks: 10, generatorInputBytes: 1_000);

        Assert.True(info.IsComplete);
        Assert.Equal(string.Empty, info.FailureReason);
    }

    [Fact]
    public void GeneratorProducedMismatchMarksIncompleteEvenWhenPipelineIsSelfConsistent()
    {
        // produced == accepted == formatted: the pipeline lost nothing it *read*,
        // but data never left the transport queue.
        PipelineMetricsSnapshot pipeline = new(
            9_615, 961_523, 9_615, 961_523, 9_615,
            10, 1_200, 20, 0, 256, 4, 0,
            ShutdownDrainState.Completed, TimeSpan.FromMilliseconds(5));

        LoadCompletenessInfo info = LoadCompletenessEvaluator.Evaluate(pipeline, generatorInputBlocks: 10_000, generatorInputBytes: 1_000_000);

        Assert.False(info.IsComplete);
        Assert.Contains("generator input", info.FailureReason);
    }

    [Fact]
    public void FaultedDrainAndStageMismatchesAreEachReported()
    {
        PipelineMetricsSnapshot pipeline = new(
            5, 500, 6, 600, 7,
            0, 0, 0, 0, 1, 1, 2,
            ShutdownDrainState.Faulted, TimeSpan.FromMilliseconds(1));

        LoadCompletenessInfo info = LoadCompletenessEvaluator.Evaluate(pipeline, generatorInputBlocks: 8, generatorInputBytes: 800);

        Assert.False(info.IsComplete);
        Assert.Contains("stage block counts", info.FailureReason);
        Assert.Contains("produced bytes", info.FailureReason);
        Assert.Contains("faults=2", info.FailureReason);
        Assert.Contains("shutdown drain=Faulted", info.FailureReason);
    }
}
