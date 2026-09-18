using System.IO.Ports;
using System.Text;

namespace DuCom.Core.Ports;

public interface IReceiveTransport
{
    event EventHandler? DataAvailable;

    int BytesAvailable { get; }

    int Read(Span<byte> destination);
}

/// <summary>Marks a transport that must be consumed by one dedicated blocking receive pump.</summary>
public interface IDedicatedReceiveTransport
{
    void WaitUntilOpen(CancellationToken cancellationToken);

    int Read(byte[] destination, int offset, int count);
}

public interface ISerialTransport : IPortLifecycleTransport, IReceiveTransport
{
    SerialPortSettings Settings { get; }

    ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);
}

/// <summary>
/// COM-specific capability for applying changed serial settings in place. Deliberately not
/// part of <see cref="ISerialTransport"/>: future non-COM transports (Telnet, virtual ports)
/// must not be forced to implement Windows SerialPort semantics. Allowed in both the Closed
/// and Open lifecycle states; applied immediately to the open handle. Implementation must
/// validate first, apply atomically, and roll back to the previous values (best effort)
/// when an apply step fails. See ADR-0004.
/// </summary>
public interface ISerialSettingsTransport
{
    void ApplySettings(SerialPortSettings settings);
}

public sealed class SerialTransportWarningEventArgs(string warning) : EventArgs
{
    public string Warning { get; } = warning;
}

public sealed class SerialPortTransport : ISerialTransport, ISerialSettingsTransport, IDedicatedReceiveTransport
{
    private readonly SerialPort _serialPort;
    private readonly SerialDisconnectSignal _disconnectSignal = new();
    private readonly object _openSignalGate = new();
    private TaskCompletionSource _openSignal = NewOpenSignal();
    private int _closing;
    private int _disposed;

    public SerialPortTransport(SerialPortSettings settings)
    {
        settings.Validate();
        Settings = settings;
        _serialPort = new SerialPort
        {
            PortName = settings.PortName,
            BaudRate = settings.BaudRate,
            DataBits = settings.DataBits,
            StopBits = settings.StopBits,
            Parity = settings.Parity,
            Handshake = settings.Handshake,
            DtrEnable = settings.DtrEnable,
            DiscardNull = settings.DiscardNull,
            Encoding = Encoding.GetEncoding(settings.EncodingName),
            ReadBufferSize = settings.ReadBufferSize,
            WriteBufferSize = settings.WriteBufferSize,
            ReadTimeout = 250,
            WriteTimeout = 1_000,
        };
        if (settings.Handshake is not Handshake.RequestToSend and not Handshake.RequestToSendXOnXOff)
        {
            _serialPort.RtsEnable = settings.RtsEnable;
        }

        _serialPort.ErrorReceived += OnErrorReceived;
    }

    public event EventHandler? DataAvailable
    {
        add { }
        remove { }
    }

    public event EventHandler<TransportDisconnectedEventArgs>? Disconnected;

    public event EventHandler<SerialTransportWarningEventArgs>? Warning;

    public SerialPortSettings Settings { get; private set; }

    public int BytesAvailable
    {
        get
        {
            if (!_serialPort.IsOpen)
            {
                return 0;
            }

            try
            {
                return _serialPort.BytesToRead;
            }
            catch (Exception exception) when (ReportOperationFailure(exception))
            {
                throw;
            }
        }
    }

    public ValueTask OpenAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask(OpenCoreAsync(cancellationToken));
    }

    public ValueTask CloseAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask(CloseCoreAsync(cancellationToken));
    }

    private async Task CloseCoreAsync(CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _closing, 1);
        try
        {
            await Task.Run(() =>
            {
                if (_serialPort.IsOpen)
                {
                    _serialPort.Close();
                }
            }, cancellationToken).ConfigureAwait(false);
            lock (_openSignalGate)
            {
                if (_openSignal.Task.IsCompleted)
                {
                    _openSignal = NewOpenSignal();
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _closing, 0);
        }
    }

    public int Read(Span<byte> destination)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        try
        {
            return _serialPort.BaseStream.Read(destination);
        }
        catch (Exception exception) when (ReportOperationFailure(exception))
        {
            throw;
        }
    }

    public void WaitUntilOpen(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        Task openSignal;
        lock (_openSignalGate)
        {
            openSignal = _openSignal.Task;
        }

        openSignal.Wait(cancellationToken);
    }

    public int Read(byte[] destination, int offset, int count)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        try
        {
            return _serialPort.Read(destination, offset, count);
        }
        catch (TimeoutException)
        {
            throw;
        }
        catch (Exception exception) when (ReportOperationFailure(exception))
        {
            throw;
        }
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask(WriteCoreAsync(data, cancellationToken));
    }

    /// <summary>
    /// Applies validated settings in place (ADR-0004 contract: valid in Closed and Open
    /// states, applied immediately). The old values are captured first; when any property
    /// apply step fails, a best-effort rollback restores the previous configuration before
    /// the exception is rethrown, so a failed update never leaves a half-applied port.
    /// </summary>
    public void ApplySettings(SerialPortSettings settings)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        if (!string.Equals(settings.PortName, Settings.PortName, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Port name cannot change while updating serial settings.", nameof(settings));
        }

        SerialPortSettings previous = Settings;
        try
        {
            ApplyPortProperties(settings);
            Settings = settings;
        }
        catch
        {
            RollbackPortProperties(previous);
            throw;
        }
    }

    private void ApplyPortProperties(SerialPortSettings settings)
    {
        _serialPort.BaudRate = settings.BaudRate;
        _serialPort.DataBits = settings.DataBits;
        _serialPort.StopBits = settings.StopBits;
        _serialPort.Parity = settings.Parity;
        _serialPort.Handshake = settings.Handshake;
        _serialPort.DtrEnable = settings.DtrEnable;
        _serialPort.DiscardNull = settings.DiscardNull;
        _serialPort.Encoding = Encoding.GetEncoding(settings.EncodingName);
        if (settings.Handshake is not Handshake.RequestToSend and not Handshake.RequestToSendXOnXOff)
        {
            _serialPort.RtsEnable = settings.RtsEnable;
        }
    }

    private void RollbackPortProperties(SerialPortSettings previous)
    {
        try
        {
            ApplyPortProperties(previous);
        }
        catch (Exception exception)
        {
            // The rollback itself failed: surface both facts instead of masking the
            // original failure. Settings keeps the previous snapshot either way.
            throw new AggregateException(
                "Applying serial settings failed and the rollback to the previous values also failed. The port may be in a mixed configuration; reopen the port to recover.",
                exception);
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            Interlocked.Exchange(ref _closing, 1);
            _serialPort.ErrorReceived -= OnErrorReceived;
            lock (_openSignalGate)
            {
                _openSignal.TrySetCanceled();
            }
            _serialPort.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private void OnErrorReceived(object sender, SerialErrorReceivedEventArgs e) =>
        Warning?.Invoke(this, new SerialTransportWarningEventArgs($"SerialWarning.{e.EventType}"));

    private async Task OpenCoreAsync(CancellationToken cancellationToken)
    {
        await Task.Run(_serialPort.Open, cancellationToken).ConfigureAwait(false);
        _disconnectSignal.MarkOpened();
        lock (_openSignalGate)
        {
            _openSignal.TrySetResult();
        }
    }

    private static TaskCompletionSource NewOpenSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private async Task WriteCoreAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        try
        {
            await _serialPort.BaseStream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (ReportOperationFailure(exception))
        {
            throw;
        }
    }

    private bool ReportOperationFailure(Exception exception)
    {
        if (_disconnectSignal.TryReport(
                exception,
                _serialPort.IsOpen,
                Volatile.Read(ref _closing) != 0,
                Volatile.Read(ref _disposed) != 0))
        {
            Disconnected?.Invoke(this, new TransportDisconnectedEventArgs(exception));
        }

        return true;
    }
}

internal sealed class SerialDisconnectSignal
{
    private int _opened;
    private int _reported;

    public void MarkOpened()
    {
        Volatile.Write(ref _opened, 1);
        Volatile.Write(ref _reported, 0);
    }

    public bool TryReport(Exception exception, bool isOpen, bool closeRequested, bool disposed)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (Volatile.Read(ref _opened) == 0 || closeRequested || disposed || !IsUnrecoverable(exception, isOpen))
        {
            return false;
        }

        return Interlocked.Exchange(ref _reported, 1) == 0;
    }

    public static bool IsUnrecoverable(Exception exception, bool isOpen) =>
        exception is IOException or UnauthorizedAccessException ||
        exception is InvalidOperationException && !isOpen;
}
