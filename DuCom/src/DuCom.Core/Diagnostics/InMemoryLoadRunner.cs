using System.Diagnostics;

namespace DuCom.Core.Diagnostics;

public interface ILoadBlockTarget
{
    ValueTask AcceptAsync(GeneratedLoadBlock block, CancellationToken cancellationToken);
}

public sealed record LoadRunResult(
    PipelineMetricsSnapshot Metrics,
    TimeSpan Elapsed,
    string? FaultMessage);

public static class InMemoryLoadRunner
{
    public static async Task<LoadRunResult> RunAsync(
        LoadGeneratorOptions options,
        ILoadBlockTarget target,
        bool pace,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(target);

        GeneratedLoadBlock[] blocks = DeterministicLoadGenerator.Generate(options).ToArray();
        LoadMetrics metrics = new();
        foreach (GeneratedLoadBlock block in blocks)
        {
            metrics.AddProducedBlock(block.Payload.Length);
        }

        Stopwatch run = Stopwatch.StartNew();
        try
        {
            foreach (GeneratedLoadBlock block in blocks)
            {
                if (pace)
                {
                    TimeSpan delay = block.ScheduledOffset - run.Elapsed;
                    if (delay > TimeSpan.Zero)
                    {
                        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                    }
                }

                await target.AcceptAsync(block, cancellationToken).ConfigureAwait(false);
                metrics.AddAcceptedBlock(block.Payload.Length);
            }

            metrics.SetShutdownDrain(ShutdownDrainState.Completed, TimeSpan.Zero);
            return new LoadRunResult(metrics.Snapshot(), run.Elapsed, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            metrics.SetShutdownDrain(ShutdownDrainState.Cancelled, TimeSpan.Zero);
            return new LoadRunResult(metrics.Snapshot(), run.Elapsed, "Load run was cancelled.");
        }
        catch (Exception exception)
        {
            metrics.AddFault();
            metrics.SetShutdownDrain(ShutdownDrainState.Faulted, TimeSpan.Zero);
            return new LoadRunResult(metrics.Snapshot(), run.Elapsed, exception.ToString());
        }
        finally
        {
            run.Stop();
        }
    }
}

public sealed class ImmediateLoadBlockTarget : ILoadBlockTarget
{
    public ValueTask AcceptAsync(GeneratedLoadBlock block, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(block);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }
}

public sealed class DelayedLoadBlockTarget(TimeSpan delay) : ILoadBlockTarget
{
    public async ValueTask AcceptAsync(GeneratedLoadBlock block, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(block);
        ArgumentOutOfRangeException.ThrowIfLessThan(delay, TimeSpan.Zero);
        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
    }
}

public sealed class FailingLoadBlockTarget(int failAfterAcceptedBlocks) : ILoadBlockTarget
{
    private int _acceptedBlocks;

    public ValueTask AcceptAsync(GeneratedLoadBlock block, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(block);
        ArgumentOutOfRangeException.ThrowIfNegative(failAfterAcceptedBlocks);
        cancellationToken.ThrowIfCancellationRequested();

        if (Interlocked.Increment(ref _acceptedBlocks) > failAfterAcceptedBlocks)
        {
            throw new IOException($"Simulated target failure after {failAfterAcceptedBlocks} accepted blocks.");
        }

        return ValueTask.CompletedTask;
    }
}
