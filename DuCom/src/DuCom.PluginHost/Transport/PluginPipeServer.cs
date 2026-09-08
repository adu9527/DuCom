using System.IO.Pipes;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using DuCom.Plugin;

namespace DuCom.PluginHost.Transport;

public sealed class PipeRateViolationException : Exception
{
    public PipeRateViolationException(string message)
        : base(message)
    {
    }
}

public sealed class PipeProtocolException : Exception
{
    public PipeProtocolException(string message)
        : base(message)
    {
    }
}

public sealed class PluginPipeServer : IAsyncDisposable
{
    private const int MaximumQueuedEventFrames = 512;
    private const long MaximumQueuedEventBytes = 4L * 1024 * 1024;
    private const int RateWindowSeconds = 10;
    private const int MaximumMessagesPerWindow = 4000;
    private const long MaximumBytesPerWindow = 32L * 1024 * 1024;

    private readonly NamedPipeServerStream _pipe;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly object _eventGate = new();
    private readonly Queue<byte[]> _eventFrames = new();

    private readonly object _rateGate = new();
    private readonly Queue<(long Timestamp, long Bytes)> _rateEntries = new();
    private long _windowBytes;
    private long _droppedEventFrames;
    private long _queuedEventBytes;
    private volatile bool _closed;

    private PluginPipeServer(NamedPipeServerStream pipe)
    {
        _pipe = pipe;
    }

    public event Action<PluginWireMessage>? MessageReceived;
    public event Action<Exception?>? ConnectionClosed;

    public long DroppedEventFrames => Interlocked.Read(ref _droppedEventFrames);

    public static PluginPipeServer Create(string pipeName, string appContainerSid)
    {
        ArgumentException.ThrowIfNullOrEmpty(pipeName);
        PipeSecurity security = new();
        security.AddAccessRule(new PipeAccessRule(
            WindowsIdentity.GetCurrent().User!,
            PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
            AccessControlType.Allow));
        if (!string.IsNullOrEmpty(appContainerSid))
        {
            security.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(appContainerSid),
                PipeAccessRights.ReadWrite,
                AccessControlType.Allow));
        }

        NamedPipeServerStream pipe = NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 64 * 1024,
            outBufferSize: 64 * 1024,
            security);
        return new PluginPipeServer(pipe);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(IntPtr pipe, out uint clientProcessId);

    public async Task<bool> WaitForConnectionAndAuthenticateAsync(
        string expectedCredential,
        uint expectedProcessId,
        string expectedSessionId,
        string expectedActivationId,
        TimeSpan timeout)
    {
        using CancellationTokenSource cancellation = new(timeout);
        try
        {
            await _pipe.WaitForConnectionAsync(cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }

        if (!_pipe.IsConnected)
        {
            return false;
        }

        if (!GetNamedPipeClientProcessId(_pipe.SafePipeHandle.DangerousGetHandle(), out uint clientProcessId))
        {
            throw new PipeProtocolException($"Cannot determine pipe client pid (Win32 error {Marshal.GetLastWin32Error()}).");
        }

        if (clientProcessId != expectedProcessId)
        {
            throw new PipeProtocolException($"Pipe client pid {clientProcessId} does not match the spawned worker pid {expectedProcessId}.");
        }

        PluginWireMessage? hello = await ReadWithTimeoutAsync(timeout).ConfigureAwait(false);
        if (hello is null || hello.Kind != WireKinds.Notification || hello.Operation != PluginOps.WorkerHello)
        {
            throw new PipeProtocolException("The first worker message must be worker.hello.");
        }

        string? credential = hello.Data?.TryGetProperty("credential", out JsonElement element) == true ? element.GetString() : null;
        if (!CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(credential ?? string.Empty),
                System.Text.Encoding.UTF8.GetBytes(expectedCredential)))
        {
            throw new PipeProtocolException("Worker credential mismatch.");
        }

        if (!string.Equals(hello.SessionId, expectedSessionId, StringComparison.Ordinal)
            || !string.Equals(hello.ActivationId, expectedActivationId, StringComparison.Ordinal))
        {
            throw new PipeProtocolException("Worker session identity mismatch.");
        }

        _ = Task.Run(EventWriterLoopAsync);
        _ = Task.Run(ReadLoopAsync);
        return true;
    }

    private async Task ReadLoopAsync()
    {
        Exception? failure = null;
        try
        {
            while (!_closed && _pipe.IsConnected)
            {
                PluginWireMessage? message = await ReadOneAsync(CancellationToken.None).ConfigureAwait(false);
                if (message is null)
                {
                    break;
                }

                if (!string.Equals(message.Kind, WireKinds.Request, StringComparison.Ordinal)
                    && !string.Equals(message.Kind, WireKinds.Response, StringComparison.Ordinal)
                    && !string.Equals(message.Kind, WireKinds.Notification, StringComparison.Ordinal))
                {
                    throw new PipeProtocolException($"Unknown message kind '{message.Kind}'.");
                }
                if (!string.Equals(message.ProtocolVersion, PluginWire.ProtocolVersion, StringComparison.Ordinal))
                {
                    throw new PipeProtocolException($"Unsupported wire protocol '{message.ProtocolVersion}'.");
                }
                if (message.Kind is WireKinds.Request or WireKinds.Response
                    && (string.IsNullOrWhiteSpace(message.RequestId) || message.RequestId.Length > 128))
                {
                    throw new PipeProtocolException("Request/response identity is malformed.");
                }
                if (message.Kind == WireKinds.Request && string.IsNullOrWhiteSpace(message.Operation))
                {
                    throw new PipeProtocolException("A request must include an operation.");
                }
                if (message.Kind == WireKinds.Notification && message.RequestId is not null)
                {
                    throw new PipeProtocolException("Notifications must not carry a request id.");
                }

                MessageReceived?.Invoke(message);
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        _closed = true;
        lock (_eventGate)
        {
            System.Threading.Monitor.PulseAll(_eventGate);
        }
        ConnectionClosed?.Invoke(failure);
    }

    private async Task EventWriterLoopAsync()
    {
        try
        {
            while (!_closed)
            {
                byte[]? frame = TakeEventFrame();
                if (frame is null)
                {
                    continue;
                }

                await _writeLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    await _pipe.WriteAsync(frame, CancellationToken.None).ConfigureAwait(false);
                    await _pipe.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                }
                finally
                {
                    _writeLock.Release();
                }
            }
        }
        catch (Exception)
        {
        }
    }

    public async Task<PluginWireMessage?> SendRequestAsync(PluginWireMessage request, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_closed)
        {
            return null;
        }

        using CancellationTokenSource cancellation = new(timeout);
        await _writeLock.WaitAsync(cancellation.Token).ConfigureAwait(false);
        try
        {
            await PluginWire.WriteAsync(_pipe, request, cancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }

        return null;
    }

    public async Task SendControlMessageAsync(PluginWireMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await PluginWire.WriteAsync(_pipe, message, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public bool TryEnqueueEventFrame(byte[] frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (_closed)
        {
            return false;
        }

        lock (_eventGate)
        {
            if (frame.Length > MaximumQueuedEventBytes)
            {
                Interlocked.Increment(ref _droppedEventFrames);
                return false;
            }
            bool discarded = false;
            while (_eventFrames.Count >= MaximumQueuedEventFrames || _queuedEventBytes + frame.Length > MaximumQueuedEventBytes)
            {
                byte[] discardedFrame = _eventFrames.Dequeue();
                _queuedEventBytes -= discardedFrame.Length;
                Interlocked.Increment(ref _droppedEventFrames);
                discarded = true;
            }

            _eventFrames.Enqueue(frame);
            _queuedEventBytes += frame.Length;
            System.Threading.Monitor.Pulse(_eventGate);
            return !discarded;
        }
    }

    private byte[]? TakeEventFrame()
    {
        lock (_eventGate)
        {
            while (_eventFrames.Count == 0)
            {
                if (_closed)
                {
                    return null;
                }

                if (!System.Threading.Monitor.Wait(_eventGate, 500))
                {
                    return null;
                }
            }

            byte[] frame = _eventFrames.Dequeue();
            _queuedEventBytes -= frame.Length;
            return frame;
        }
    }

    private async Task<PluginWireMessage?> ReadWithTimeoutAsync(TimeSpan timeout)
    {
        using CancellationTokenSource cancellation = new(timeout);
        try
        {
            return await ReadOneAsync(cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private async Task<PluginWireMessage?> ReadOneAsync(CancellationToken cancellationToken)
    {
        byte[] header = new byte[4];
        if (!await ReadExactlyAsync(header, cancellationToken))
        {
            return null;
        }

        uint length = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length > PluginWire.MaximumFrameBytes)
        {
            throw new PipeProtocolException($"Frame length {length} exceeds the {PluginWire.MaximumFrameBytes} byte limit.");
        }

        byte[] payload = new byte[length];
        if (length > 0 && !await ReadExactlyAsync(payload, cancellationToken))
        {
            return null;
        }

        // Charge the original frame, including its header, before parsing untrusted JSON.
        CountRate(header.Length + (long)length, Environment.TickCount64);
        PluginWireMessage? message = JsonSerializer.Deserialize<PluginWireMessage>(payload, PluginWire.JsonOptions);
        if (message is null)
            throw new PipeProtocolException("Frame does not contain a message.");
        return message;
    }

    private async Task<bool> ReadExactlyAsync(byte[] buffer, CancellationToken cancellationToken)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await _pipe.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return false;
            }

            total += read;
        }

        return true;
    }

    private void CountRate(long bytes, long now)
    {
        lock (_rateGate)
        {
            long windowStart = now - RateWindowSeconds * 1000;
            while (_rateEntries.Count > 0 && _rateEntries.Peek().Timestamp < windowStart)
            {
                _windowBytes -= _rateEntries.Dequeue().Bytes;
            }

            _rateEntries.Enqueue((now, bytes));
            _windowBytes += bytes;
            if (_rateEntries.Count > MaximumMessagesPerWindow)
            {
                throw new PipeRateViolationException($"Worker sent more than {MaximumMessagesPerWindow} messages in {RateWindowSeconds}s.");
            }

            if (_windowBytes > MaximumBytesPerWindow)
            {
                throw new PipeRateViolationException($"Worker sent more than {MaximumBytesPerWindow} bytes in {RateWindowSeconds}s.");
            }
        }
    }

    public void Close()
    {
        _closed = true;
        lock (_eventGate)
        {
            System.Threading.Monitor.PulseAll(_eventGate);
        }
        try
        {
            if (_pipe.IsConnected)
            {
                _pipe.Disconnect();
            }
        }
        catch (Exception)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        Close();
        await _pipe.DisposeAsync().ConfigureAwait(false);
        _writeLock.Dispose();
    }
}


