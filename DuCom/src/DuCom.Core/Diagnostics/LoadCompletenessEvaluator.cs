namespace DuCom.Core.Diagnostics;

/// <summary>
/// Close-gate evidence for a serial-session load run: the per-session close results, the
/// first session fault (if any), and the actual on-disk log files.
/// </summary>
public sealed record SessionCloseGate(
    bool AllSessionsClosedCleanly,
    string? SessionFaultMessage,
    bool LogFilesExist,
    long ActualLogBytes);

/// <summary>
/// Cross-checks what the load generator actually pushed into a transport against what the
/// receive pipeline counted as produced/accepted/formatted/written, the session close
/// results, and the bytes actually present in the log directory. A close that strands data
/// inside the transport queue shows up here as generator-input != pipeline-produced; a
/// writer that lost records shows up as written/file byte mismatches.
/// </summary>
public static class LoadCompletenessEvaluator
{
    public static LoadCompletenessInfo Evaluate(
        PipelineMetricsSnapshot pipeline,
        long generatorInputBlocks,
        long generatorInputBytes,
        SessionCloseGate? closeGate = null)
    {
        bool blocksMatchAllStages = pipeline.ProducedBlocks == pipeline.AcceptedBlocks &&
                                    pipeline.AcceptedBlocks == pipeline.FormattedLogBlocks;
        bool bytesMatchProducedAccepted = pipeline.ProducedBytes == pipeline.AcceptedBytes;
        bool generatorMatchesPipeline = generatorInputBlocks == pipeline.ProducedBlocks &&
                                        generatorInputBytes == pipeline.ProducedBytes;
        bool noFaults = pipeline.Faults == 0;
        bool drainCompleted = pipeline.ShutdownDrainState == ShutdownDrainState.Completed;

        string reason = (blocksMatchAllStages && bytesMatchProducedAccepted && generatorMatchesPipeline && noFaults && drainCompleted)
            ? string.Empty
            : BuildFailureReason(pipeline, generatorInputBlocks, generatorInputBytes, blocksMatchAllStages, bytesMatchProducedAccepted, generatorMatchesPipeline, noFaults, drainCompleted);

        bool closeSucceeded = closeGate?.AllSessionsClosedCleanly ?? true;
        bool sessionFaultFree = closeGate?.SessionFaultMessage is null;
        bool logFilesExist = closeGate?.LogFilesExist ?? true;
        long actualLogBytes = closeGate?.ActualLogBytes ?? 0;
        bool writtenBytesPresent = closeGate is null || generatorInputBytes <= 0 || pipeline.WrittenLogBytes > 0;
        bool fileBytesMatchWritten = closeGate is null || actualLogBytes == pipeline.WrittenLogBytes;

        if (closeGate is not null && closeSucceeded && sessionFaultFree && logFilesExist && writtenBytesPresent && fileBytesMatchWritten && reason.Length == 0)
        {
            return Complete(pipeline, generatorInputBlocks, generatorInputBytes, closeSucceeded, sessionFaultFree, logFilesExist, actualLogBytes);
        }

        if (closeGate is not null)
        {
            List<string> gateProblems = [];
            if (!writtenBytesPresent)
            {
                gateProblems.Add($"generator input {generatorInputBytes} bytes > 0 but written log bytes {pipeline.WrittenLogBytes}");
            }

            if (!logFilesExist)
            {
                gateProblems.Add("log files missing from the session log directory");
            }

            if (logFilesExist && !fileBytesMatchWritten)
            {
                gateProblems.Add($"actual log file bytes {actualLogBytes} != written log bytes {pipeline.WrittenLogBytes}");
            }

            if (!closeSucceeded)
            {
                gateProblems.Add("session close did not complete cleanly (expected Succeeded/AlreadyClosed)");
            }

            if (!sessionFaultFree)
            {
                gateProblems.Add($"session fault: {closeGate.SessionFaultMessage}");
            }

            if (reason.Length > 0)
            {
                gateProblems.Add(reason);
            }

            return new LoadCompletenessInfo(
                generatorInputBlocks,
                generatorInputBytes,
                pipeline.ProducedBlocks,
                pipeline.ProducedBytes,
                IsComplete: false,
                string.Join("; ", gateProblems),
                pipeline.WrittenLogBytes,
                pipeline.WrittenLogRecords,
                closeSucceeded,
                sessionFaultFree,
                logFilesExist,
                actualLogBytes);
        }

        return new LoadCompletenessInfo(
            generatorInputBlocks,
            generatorInputBytes,
            pipeline.ProducedBlocks,
            pipeline.ProducedBytes,
            reason.Length == 0,
            reason,
            pipeline.WrittenLogBytes,
            pipeline.WrittenLogRecords,
            closeSucceeded,
            sessionFaultFree,
            logFilesExist,
            actualLogBytes);
    }

    private static LoadCompletenessInfo Complete(
        PipelineMetricsSnapshot pipeline,
        long generatorInputBlocks,
        long generatorInputBytes,
        bool closeSucceeded,
        bool sessionFaultFree,
        bool logFilesExist,
        long actualLogBytes) => new(
            generatorInputBlocks,
            generatorInputBytes,
            pipeline.ProducedBlocks,
            pipeline.ProducedBytes,
            IsComplete: true,
            string.Empty,
            pipeline.WrittenLogBytes,
            pipeline.WrittenLogRecords,
            closeSucceeded,
            sessionFaultFree,
            logFilesExist,
            actualLogBytes);

    private static string BuildFailureReason(
        PipelineMetricsSnapshot pipeline,
        long generatorInputBlocks,
        long generatorInputBytes,
        bool blocksMatchAllStages,
        bool bytesMatchProducedAccepted,
        bool generatorMatchesPipeline,
        bool noFaults,
        bool drainCompleted)
    {
        List<string> problems = [];
        if (!blocksMatchAllStages)
        {
            problems.Add($"stage block counts differ: produced={pipeline.ProducedBlocks}, accepted={pipeline.AcceptedBlocks}, formatted={pipeline.FormattedLogBlocks}");
        }

        if (!bytesMatchProducedAccepted)
        {
            problems.Add($"produced bytes {pipeline.ProducedBytes} != accepted bytes {pipeline.AcceptedBytes}");
        }

        if (!generatorMatchesPipeline)
        {
            problems.Add($"generator input {generatorInputBlocks} blocks/{generatorInputBytes} bytes != pipeline produced {pipeline.ProducedBlocks} blocks/{pipeline.ProducedBytes} bytes");
        }

        if (!noFaults)
        {
            problems.Add($"faults={pipeline.Faults}");
        }

        if (!drainCompleted)
        {
            problems.Add($"shutdown drain={pipeline.ShutdownDrainState}");
        }

        return string.Join("; ", problems);
    }
}

public sealed record LoadCompletenessInfo(
    long GeneratorInputBlocks,
    long GeneratorInputBytes,
    long PipelineProducedBlocks,
    long PipelineProducedBytes,
    bool IsComplete,
    string FailureReason,
    long WrittenLogBytes = 0,
    long WrittenLogRecords = 0,
    bool CloseSucceeded = true,
    bool SessionFaultFree = true,
    bool LogFilesExist = true,
    long ActualLogBytes = 0);
