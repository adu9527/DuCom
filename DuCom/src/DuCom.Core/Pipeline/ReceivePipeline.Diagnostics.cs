using System.Diagnostics;
using DuCom.Core.Diagnostics;

namespace DuCom.Core.Pipeline;

public sealed partial class ReceivePipeline
{
    private long BeginDiagnosticBatch(string trigger, double callbackGapMilliseconds, int initialBytesAvailable)
    {
        if (_diagnosticObserver is null)
        {
            return 0;
        }

        long id = Interlocked.Increment(ref _diagnosticBatchSequence);
        lock (_diagnosticGate)
        {
            _diagnosticBatches[id] = new ReceiveDiagnosticBatch(
                trigger,
                callbackGapMilliseconds,
                initialBytesAvailable);
        }
        return id;
    }

    private void CompleteDiagnosticRead(
        long id,
        int readBlocks,
        int readBytes,
        int maximumReadBytes,
        int queueDepthPeak,
        int remainingBytesAvailable,
        bool capacityLimited)
    {
        if (id == 0)
        {
            return;
        }

        ReceiveDiagnosticSnapshot? snapshot;
        lock (_diagnosticGate)
        {
            if (!_diagnosticBatches.TryGetValue(id, out ReceiveDiagnosticBatch? batch))
            {
                return;
            }

            batch.ReadCompleted = true;
            batch.ReadBlocks = readBlocks;
            batch.ReadBytes = readBytes;
            batch.MaximumReadBytes = maximumReadBytes;
            batch.QueueDepthPeak = queueDepthPeak;
            batch.RemainingBytesAvailable = remainingBytesAvailable;
            batch.CapacityLimited = capacityLimited;
            snapshot = TryCompleteDiagnosticBatch(id, batch);
        }
        PublishDiagnostic(snapshot);
    }

    private void CompleteDiagnosticProcessing(long id, int formattedLines, double processingMilliseconds)
    {
        if (id == 0)
        {
            return;
        }

        ReceiveDiagnosticSnapshot? snapshot;
        lock (_diagnosticGate)
        {
            if (!_diagnosticBatches.TryGetValue(id, out ReceiveDiagnosticBatch? batch))
            {
                return;
            }

            batch.ProcessedBlocks++;
            batch.FormattedLines += formattedLines;
            batch.ProcessingMilliseconds += processingMilliseconds;
            snapshot = TryCompleteDiagnosticBatch(id, batch);
        }
        PublishDiagnostic(snapshot);
    }

    private ReceiveDiagnosticSnapshot? TryCompleteDiagnosticBatch(long id, ReceiveDiagnosticBatch batch)
    {
        if (!batch.ReadCompleted || batch.ProcessedBlocks != batch.ReadBlocks)
        {
            return null;
        }

        _diagnosticBatches.Remove(id);
        return new ReceiveDiagnosticSnapshot(
            _diagnosticPortName,
            batch.Trigger,
            batch.CallbackGapMilliseconds,
            batch.InitialBytesAvailable,
            batch.ReadBlocks,
            batch.ReadBytes,
            batch.MaximumReadBytes,
            batch.QueueDepthPeak,
            batch.RemainingBytesAvailable,
            batch.CapacityLimited,
            batch.FormattedLines,
            batch.ProcessingMilliseconds);
    }

    private void PublishDiagnostic(ReceiveDiagnosticSnapshot? snapshot)
    {
        if (snapshot is null || !ShouldPublishDiagnostic(snapshot))
        {
            return;
        }

        long now = Stopwatch.GetTimestamp();
        long next = Volatile.Read(ref _nextDiagnosticPublishTimestamp);
        if (now < next || Interlocked.CompareExchange(
                ref _nextDiagnosticPublishTimestamp,
                now + Stopwatch.Frequency / 4,
                next) != next)
        {
            return;
        }

        try
        {
            _diagnosticObserver?.Invoke(snapshot);
        }
        catch
        {
            // Diagnostics must never affect receive processing.
        }
    }

    internal static bool ShouldPublishDiagnostic(ReceiveDiagnosticSnapshot snapshot) =>
        snapshot.CapacityLimited ||
        snapshot.QueueDepthPeak >= 8 ||
        snapshot.ReadBytes >= 16 * 1024 ||
        snapshot.ProcessingMilliseconds >= 50 ||
        snapshot.CallbackGapMilliseconds >= 250 && snapshot.ReadBytes >= 1024;

    private sealed class ReceiveDiagnosticBatch(
        string trigger,
        double callbackGapMilliseconds,
        int initialBytesAvailable)
    {
        public string Trigger { get; } = trigger;
        public double CallbackGapMilliseconds { get; } = callbackGapMilliseconds;
        public int InitialBytesAvailable { get; } = initialBytesAvailable;
        public bool ReadCompleted { get; set; }
        public int ReadBlocks { get; set; }
        public int ReadBytes { get; set; }
        public int MaximumReadBytes { get; set; }
        public int QueueDepthPeak { get; set; }
        public int RemainingBytesAvailable { get; set; }
        public bool CapacityLimited { get; set; }
        public int ProcessedBlocks { get; set; }
        public int FormattedLines { get; set; }
        public double ProcessingMilliseconds { get; set; }
    }
}
