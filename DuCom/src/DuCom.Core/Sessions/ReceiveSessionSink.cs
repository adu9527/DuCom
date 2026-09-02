using DuCom.Core.Diagnostics;
using DuCom.Core.Logging;
using DuCom.Core.Parsing;
using DuCom.Core.Pipeline;
using DuCom.Core.Storage;

namespace DuCom.Core.Sessions;

public sealed class ReceiveSessionSink(
    SessionLogWriter logWriter,
    BudgetedLineStore lineStore,
    LoadMetrics metrics,
    SessionTapHub? displayTaps = null) : IReceiveBlockSink, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _formatterLock = new(1, 1);
    private StatefulReceiveFormatter? _formatter;
    private ReceiveFormattingProfile? _activeProfile;
    private FormattedLine? _pendingLine;
    private long? _softWrappedLogicalId;

    public FormattedLine? PendingLine
    {
        get
        {
            lock (_gate)
            {
                return _pendingLine;
            }
        }
    }

    public async ValueTask ProcessAsync(ReceiveBlock block, CancellationToken cancellationToken)
    {
        await _formatterLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SwitchProfileAsync(block.FormattingProfile, cancellationToken).ConfigureAwait(false);
            IReadOnlyList<FormattedLine> lines = _formatter!.Append(block.Memory.Span, block.ReceivedAtUtc);
            await CommitAsync(lines, commitUnterminated: false, cancellationToken).ConfigureAwait(false);
            displayTaps?.PublishReceive(block.Memory.Span, block.ReceivedAtUtc, block.FormattingProfile);
            metrics.AddFormattedLogBlock();
        }
        finally
        {
            _formatterLock.Release();
        }
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        await _formatterLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_formatter is not null)
            {
                await CommitAsync(_formatter.Flush(), commitUnterminated: true, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _formatterLock.Release();
        }
    }

    private async Task SwitchProfileAsync(ReceiveFormattingProfile profile, CancellationToken cancellationToken)
    {
        if (_activeProfile == profile)
        {
            return;
        }

        if (_formatter is not null)
        {
            await CommitAsync(_formatter.Flush(), commitUnterminated: true, cancellationToken).ConfigureAwait(false);
        }

        _formatter = profile.CreateFormatter();
        _activeProfile = profile;
    }

    public ValueTask DisposeAsync()
    {
        _formatterLock.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task CommitAsync(
        IReadOnlyList<FormattedLine> lines,
        bool commitUnterminated,
        CancellationToken cancellationToken)
    {
        foreach (FormattedLine line in lines)
        {
            if (line.IsTerminated || line.IsSoftWrapped || commitUnterminated)
            {
                if (_softWrappedLogicalId.HasValue && line.IsTerminated && line.Text.Length == 0)
                {
                    lineStore.CompleteContinuation(_softWrappedLogicalId.Value);
                }
                else if (_softWrappedLogicalId.HasValue)
                {
                    lineStore.AppendContinuation(_softWrappedLogicalId.Value, line.Text, line.IsTerminated);
                }
                else
                {
                    _softWrappedLogicalId = lineStore.Append(LineDirection.Rx, line.ReceivedAtUtc, line.Text, line.IsTerminated);
                }

                metrics.AddLineRecords(1);
                string logText = line.IsTerminated ? line.Text + "\r\n" : line.Text;
                if (!await logWriter.WriteAsync(new FormattedLogRecord(logText), cancellationToken).ConfigureAwait(false))
                {
                    throw new IOException("Formatted log writer rejected an accepted receive line.");
                }

                lock (_gate)
                {
                    _pendingLine = null;
                }

                if (line.IsTerminated || commitUnterminated && !line.IsSoftWrapped)
                {
                    _softWrappedLogicalId = null;
                }
            }
            else
            {
                lock (_gate)
                {
                    _pendingLine = line;
                }
            }
        }
    }
}
