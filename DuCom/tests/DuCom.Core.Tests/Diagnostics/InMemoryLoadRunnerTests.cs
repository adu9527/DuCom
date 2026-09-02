using DuCom.Core.Diagnostics;

namespace DuCom.Core.Tests.Diagnostics;

public sealed class InMemoryLoadRunnerTests
{
    [Fact]
    public async Task SuccessfulTargetAccountsForEveryProducedBlockWithoutClaimingLogWrites()
    {
        LoadGeneratorOptions options = CreateOptions();
        RecordingTarget target = new();

        LoadRunResult result = await InMemoryLoadRunner.RunAsync(options, target, pace: false);

        Assert.Equal(result.Metrics.ProducedBlocks, result.Metrics.AcceptedBlocks);
        Assert.Equal(0, result.Metrics.FormattedLogBlocks);
        Assert.Equal(0, result.Metrics.WrittenLogRecords);
        Assert.True(result.Metrics.IsInputAcceptanceComplete);
        Assert.False(result.Metrics.IsLogFormattingCoverageComplete);
        Assert.Equal(result.Metrics.ProducedBlocks, target.Blocks.Count);
        Assert.Equal(ShutdownDrainState.Completed, result.Metrics.ShutdownDrainState);
        Assert.Null(result.FaultMessage);
    }

    [Fact]
    public async Task SlowTargetProcessesAllBlocksAndRunnerWaitsForIt()
    {
        LoadGeneratorOptions options = CreateOptions();
        RecordingTarget target = new(TimeSpan.FromMilliseconds(2));

        LoadRunResult result = await InMemoryLoadRunner.RunAsync(options, target, pace: false);

        Assert.Equal(result.Metrics.ProducedBlocks, target.Blocks.Count);
        Assert.True(result.Elapsed >= TimeSpan.FromMilliseconds(target.Blocks.Count));
        Assert.Equal(ShutdownDrainState.Completed, result.Metrics.ShutdownDrainState);
    }

    [Fact]
    public async Task FailingTargetStopsAndReportsExplicitFault()
    {
        LoadGeneratorOptions options = CreateOptions();
        RecordingTarget target = new(failAtSequence: 2);

        LoadRunResult result = await InMemoryLoadRunner.RunAsync(options, target, pace: false);

        Assert.Equal(1, result.Metrics.Faults);
        Assert.Equal(ShutdownDrainState.Faulted, result.Metrics.ShutdownDrainState);
        Assert.NotNull(result.FaultMessage);
        Assert.True(result.Metrics.AcceptedBlocks < result.Metrics.ProducedBlocks);
        Assert.False(result.Metrics.IsInputAcceptanceComplete);
    }

    [Fact]
    public async Task PacedRunWaitsUntilTheLastScheduledOffset()
    {
        LoadGeneratorOptions options = CreateOptions();
        TimeSpan lastOffset = DeterministicLoadGenerator.Generate(options).Max(block => block.ScheduledOffset);

        LoadRunResult result = await InMemoryLoadRunner.RunAsync(options, new RecordingTarget(), pace: true);

        Assert.True(result.Elapsed >= lastOffset - TimeSpan.FromMilliseconds(5));
        Assert.Equal(TimeSpan.Zero, result.Metrics.ShutdownDrainDuration);
    }

    private static LoadGeneratorOptions CreateOptions() => new(
        Seed: 42,
        Duration: TimeSpan.FromMilliseconds(50),
        TargetBytesPerSecondPerPort: 10_000,
        MinimumChunkSize: 64,
        MaximumChunkSize: 128,
        PortCount: 2,
        PayloadProfile: LoadPayloadProfile.MixedNewline);

    private sealed class RecordingTarget(TimeSpan delay = default, int? failAtSequence = null) : ILoadBlockTarget
    {
        public List<GeneratedLoadBlock> Blocks { get; } = [];

        public async ValueTask AcceptAsync(GeneratedLoadBlock block, CancellationToken cancellationToken)
        {
            if (block.Sequence == failAtSequence)
            {
                throw new IOException("Simulated target failure.");
            }

            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken);
            }

            Blocks.Add(block);
        }
    }
}
