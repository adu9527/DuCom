using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using DuCom.Core.Diagnostics;
using DuCom.Core.Logging;
using DuCom.Core.Parsing;
using DuCom.Core.Pipeline;
using DuCom.Core.Ports;
using DuCom.Core.Sending;
using DuCom.Core.Storage;

namespace DuCom.Core.Sessions;

public sealed partial class SerialSession : IAsyncDisposable
{
    private const int DefaultMaximumSegmentCharacters = 16 * 1024;
    private const int DefaultReceiveCapacity = 256;
    private const int DefaultMaximumReadSize = 16 * 1024;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly object _disposeGate = new();
    private readonly ISerialTransport _transport;
    private SerialPortSettings _settings;
    private readonly ReceiveDisplayMode _receiveDisplayMode;
    private readonly bool _timestampEnabled;
    private readonly string _timestampFormat;
    private long _formattingProfileVersion;
    private readonly SessionLogWriterOptions _logOptions;
    private readonly bool _sendPrefixEnabled;
    private readonly string _sendPrefix;
    private readonly LoadMetrics _metrics = new();
    private readonly PortLifecycle _lifecycle;
    private readonly BudgetedLineStore _lineStore;
    private readonly SessionTapHub _displayTaps = new();
    private readonly SessionRawTapHub _rawTaps = new();
    private SessionRuntime? _runtime;
    private SessionFaultSnapshot? _fault;
    private Task? _disposeTask;
    private int _disposed;

    public SerialSession(
        ISerialTransport transport,
        SerialPortSettings settings,
        ReceiveDisplayMode receiveDisplayMode,
        bool timestampEnabled,
        SessionLogWriterOptions logOptions,
        int lineBudgetBytes,
        bool sendPrefixEnabled = true,
        string sendPrefix = "TX > ",
        string timestampFormat = "HH:mm:ss.fff")
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        settings.Validate();
        if (!Enum.IsDefined(receiveDisplayMode))
        {
            throw new ArgumentOutOfRangeException(nameof(receiveDisplayMode));
        }

        logOptions.Validate();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(lineBudgetBytes);
        _settings = settings;
        _receiveDisplayMode = receiveDisplayMode;
        _timestampEnabled = timestampEnabled;
        ArgumentException.ThrowIfNullOrWhiteSpace(timestampFormat);
        _timestampFormat = timestampFormat;
        _logOptions = logOptions;
        _sendPrefixEnabled = sendPrefixEnabled;
        _sendPrefix = sendPrefix ?? string.Empty;
        _lineStore = new BudgetedLineStore(lineBudgetBytes, DefaultMaximumSegmentCharacters);
        _lifecycle = new PortLifecycle(settings.PortName, transport);
        _transport.Disconnected += OnTransportDisconnected;
    }

    public SerialSessionSnapshot Snapshot()
    {
        SessionRuntime? runtime = Volatile.Read(ref _runtime);
        SessionFaultSnapshot? fault = Volatile.Read(ref _fault)
            ?? CreateFault("Lifecycle", _lifecycle.Snapshot.FaultMessage)
            ?? CreateFault("ReceivePipeline", runtime?.Pipeline.Fault)
            ?? CreateFault("SessionLogWriter", runtime?.LogWriter.Fault);
        return new SerialSessionSnapshot(
            _lifecycle.Snapshot,
            _lineStore.Snapshot(),
            _metrics.Snapshot(),
            fault);
    }

    public SerialSessionStatusSnapshot Status()
    {
        SerialSessionSnapshot snapshot = SnapshotWithoutLines();
        return new SerialSessionStatusSnapshot(snapshot.State, snapshot.Metrics, snapshot.Fault);
    }

    public SerialPortSettings Settings => _settings;

    public string LogDirectory => Volatile.Read(ref _runtime)?.LogWriter.OutputDirectory
        ?? _logOptions.GetOutputDirectory(DateTimeOffset.Now);

    public string? CurrentLogFilePath => Volatile.Read(ref _runtime)?.LogWriter.CurrentFilePath;

    public Task<IReadOnlyList<SessionLogFileSnapshot>> CreateLogSnapshotAsync(CancellationToken cancellationToken = default) =>
        Volatile.Read(ref _runtime)?.LogWriter.CreateSnapshotAsync(cancellationToken)
        ?? Task.FromResult<IReadOnlyList<SessionLogFileSnapshot>>([]);

    public async Task ApplySettingsAsync(SerialPortSettings settings, CancellationToken cancellationToken = default)
    {
        settings.Validate();
        if (!string.Equals(settings.PortName, _settings.PortName, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Port name cannot change while updating a serial session.", nameof(settings));
        }

        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_transport is not ISerialSettingsTransport configurable)
            {
                // ADR-0004: in-place settings updates are a COM-transport capability, not
                // part of the transport-neutral contract.
                throw new NotSupportedException("This transport does not support in-place serial settings updates.");
            }

            SerialPortSettings previous = _settings;
            bool encodingChanged = _runtime is not null &&
                !string.Equals(settings.EncodingName, _settings.EncodingName, StringComparison.OrdinalIgnoreCase);
            ReceiveFormattingProfile? replacementProfile = encodingChanged
                ? CreateFormattingProfile(settings.EncodingName, _formattingProfileVersion + 1)
                : null;

            try
            {
                configurable.ApplySettings(settings);
                if (replacementProfile is not null)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    _runtime!.Pipeline.UpdateFormattingProfile(replacementProfile);
                    _formattingProfileVersion = replacementProfile.Version;
                }

                _settings = settings;
            }
            catch (Exception failure)
            {
                try
                {
                    configurable.ApplySettings(previous);
                }
                catch (Exception rollbackFailure)
                {
                    throw new AggregateException(
                        "Applying serial settings failed and restoring the previous settings also failed. The port may be in a mixed configuration; reopen the port to recover.",
                        failure,
                        rollbackFailure);
                }

                throw;
            }
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private SerialSessionSnapshot SnapshotWithoutLines()
    {
        SessionRuntime? runtime = Volatile.Read(ref _runtime);
        SessionFaultSnapshot? fault = Volatile.Read(ref _fault)
            ?? CreateFault("Lifecycle", _lifecycle.Snapshot.FaultMessage)
            ?? CreateFault("ReceivePipeline", runtime?.Pipeline.Fault)
            ?? CreateFault("SessionLogWriter", runtime?.LogWriter.Fault);
        return new SerialSessionSnapshot(
            _lifecycle.Snapshot,
            new LineStoreSnapshot(null, null, 0, []),
            _metrics.Snapshot(),
            fault);
    }

    public async ValueTask SendAsync(
        SendMode mode,
        string text,
        NewlinePolicy newline,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_lifecycle.Snapshot.State != PortLifecycleState.Open || _runtime is null)
            {
                throw new InvalidOperationException("Serial session must be open before sending.");
            }

            Encoding encoding = Encoding.GetEncoding(_settings.EncodingName);
            byte[] payload = mode switch
            {
                SendMode.Str => SendPayloadEncoder.EncodeString(text, encoding, newline),
                SendMode.Hex => SendPayloadEncoder.EncodeHex(text, newline),
                _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
            };

            await _transport.WriteAsync(payload, cancellationToken).ConfigureAwait(false);

            _displayTaps.NotifySent(mode);
            string displayText = mode == SendMode.Str ? text : FormatHex(payload);
            string recordText = _sendPrefixEnabled ? _sendPrefix + displayText : displayText;
            if (!await _runtime.LogWriter.WriteAsync(
                    new FormattedLogRecord(recordText + "\r\n"),
                    CancellationToken.None).ConfigureAwait(false))
            {
                IOException exception = new("Session log writer rejected a transmitted record.", _runtime.LogWriter.Fault);
                SetFault("SessionLogWriter", exception);
                throw exception;
            }

            _lineStore.Append(LineDirection.Tx, DateTimeOffset.UtcNow, recordText, isTerminated: true);
            _displayTaps.PublishTransmit(recordText);
            _metrics.AddLineRecords(1);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private static SessionFaultSnapshot? CreateFault(string source, Exception? exception) =>
        exception is null ? null : new SessionFaultSnapshot(source, exception.Message);

    private static SessionFaultSnapshot? CreateFault(string source, string? message) =>
        string.IsNullOrWhiteSpace(message) ? null : new SessionFaultSnapshot(source, message);

    private static string FormatHex(ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty)
        {
            return string.Empty;
        }

        StringBuilder builder = new(payload.Length * 3 - 1);
        foreach (byte value in payload)
        {
            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            builder.Append(value.ToString("X2", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

}
