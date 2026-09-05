using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using DuCom.Core.Diagnostics;

namespace DuCom.Core.Logging;

public readonly record struct FormattedLogRecord(string Text, bool BypassRotation = false);

public enum SessionLogFlushReason
{
    Periodic,
    Rotation,
    Close,
    Fault,
}

public interface ISessionLogWriterObserver
{
    void Flushed(SessionLogFlushReason reason);
}

public sealed record SessionLogWriterOptions(
    string DirectoryPath,
    string SessionName,
    long RotationBytes = 40L * 1024 * 1024,
    int QueueCapacity = 4_096,
    bool Enabled = true,
    string FileNameFormat = "{Port}-{yyyy}-{MM}-{dd} {HH}-{mm}-{ss}.{fff}",
    bool RotationEnabled = true,
    TimeSpan? FlushInterval = null,
    bool UseDateSubdirectory = false)
{
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(DirectoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(SessionName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(RotationBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(QueueCapacity);
        ArgumentException.ThrowIfNullOrWhiteSpace(FileNameFormat);
        if (FlushInterval is { } flushInterval)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(flushInterval, TimeSpan.Zero);
        }
    }

    public string GetOutputDirectory(DateTimeOffset now)
    {
        if (!UseDateSubdirectory)
        {
            return DirectoryPath;
        }

        string folderName = $"{now.Year}-{now.Month}M-{now.Day}D";
        return Path.Combine(DirectoryPath, folderName);
    }
}

public sealed class SessionLogWriter : IAsyncDisposable
{
    private const int BatchSizeBytes = 64 * 1024;
    private static readonly TimeSpan BatchDelay = TimeSpan.FromMilliseconds(25);
    private static readonly TimeSpan DefaultFlushInterval = TimeSpan.FromMilliseconds(500);
    private readonly Channel<FormattedLogRecord> _channel;
    private readonly LoadMetrics _metrics;
    private readonly SessionLogWriterOptions _options;
    private readonly ISessionLogWriterObserver? _observer;
    private readonly TimeSpan _flushInterval;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _writerTask;
    private string? _currentFilePath;
    private int _started;
    private int _stopped;
    private int _queuedRecords;

    public SessionLogWriter(
        SessionLogWriterOptions options,
        LoadMetrics metrics,
        ISessionLogWriterObserver? observer = null)
    {
        options.Validate();
        _options = options;
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _observer = observer;
        _flushInterval = options.FlushInterval ?? DefaultFlushInterval;
        OutputDirectory = options.GetOutputDirectory(DateTimeOffset.Now);
        _channel = Channel.CreateBounded<FormattedLogRecord>(new BoundedChannelOptions(options.QueueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false,
        });
    }

    public Exception? Fault { get; private set; }

    public string OutputDirectory { get; }

    public string? CurrentFilePath => Volatile.Read(ref _currentFilePath);

    public async Task StartAsync()
    {
        if (!_options.Enabled)
        {
            _ready.TrySetResult();
            return;
        }

        if (Interlocked.Exchange(ref _started, 1) == 0)
        {
            _writerTask = Task.Run(WriteLoopAsync);
        }

        await _ready.Task.ConfigureAwait(false);
    }

    public async ValueTask<bool> WriteAsync(FormattedLogRecord record, CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            return true;
        }

        if (Fault is not null || Volatile.Read(ref _stopped) != 0)
        {
            return false;
        }

        try
        {
            await _channel.Writer.WriteAsync(record, cancellationToken).ConfigureAwait(false);
            int queued = Interlocked.Increment(ref _queuedRecords);
            _metrics.ObserveLogQueueDepth(queued);
            return true;
        }
        catch (ChannelClosedException)
        {
            return false;
        }
    }

    public async Task StopAsync()
    {
        if (!_options.Enabled)
        {
            return;
        }

        if (Interlocked.Exchange(ref _stopped, 1) == 0)
        {
            _channel.Writer.TryComplete();
        }

        if (_writerTask is not null)
        {
            await _writerTask.ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private async Task WriteLoopAsync()
    {
        StreamWriter? writer = null;
        long currentBytes = 0;
        int segment = 0;
        Stopwatch drain = Stopwatch.StartNew();
        string outputDirectory = OutputDirectory;

        try
        {
            Directory.CreateDirectory(outputDirectory);
            _ready.TrySetResult();
            List<(FormattedLogRecord Record, int Bytes)> batch = [];
            Stopwatch flushClock = Stopwatch.StartNew();
            Task<bool>? waitToRead = null;
            while (true)
            {
                waitToRead ??= _channel.Reader.WaitToReadAsync().AsTask();
                Task flushDue = Task.Delay(_flushInterval);
                if (await Task.WhenAny(waitToRead, flushDue).ConfigureAwait(false) == flushDue)
                {
                    if (writer is not null && flushClock.Elapsed >= _flushInterval)
                    {
                        await FlushAsync(SessionLogFlushReason.Periodic).ConfigureAwait(false);
                        flushClock.Restart();
                    }

                    continue;
                }

                if (!await waitToRead.ConfigureAwait(false))
                {
                    break;
                }

                waitToRead = null;

                batch.Clear();
                int batchBytes = 0;
                Task delay = Task.Delay(BatchDelay);
                while (batchBytes < BatchSizeBytes)
                {
                    while (batchBytes < BatchSizeBytes && _channel.Reader.TryRead(out FormattedLogRecord record))
                    {
                        Interlocked.Decrement(ref _queuedRecords);
                        int recordBytes = Encoding.UTF8.GetByteCount(record.Text);
                        batch.Add((record, recordBytes));
                        batchBytes += recordBytes;
                    }

                    if (batchBytes >= BatchSizeBytes || delay.IsCompleted || _channel.Reader.Completion.IsCompleted)
                    {
                        break;
                    }

                    Task<bool> moreAvailable = _channel.Reader.WaitToReadAsync().AsTask();
                    if (await Task.WhenAny(moreAvailable, delay).ConfigureAwait(false) == delay)
                    {
                        break;
                    }

                    if (!await moreAvailable.ConfigureAwait(false))
                    {
                        break;
                    }
                }

                await WriteBatchAsync(batch).ConfigureAwait(false);
                if (writer is not null && flushClock.Elapsed >= _flushInterval)
                {
                    await FlushAsync(SessionLogFlushReason.Periodic).ConfigureAwait(false);
                    flushClock.Restart();
                }
            }

            if (writer is not null)
            {
                await FlushAsync(SessionLogFlushReason.Close).ConfigureAwait(false);
                await writer.DisposeAsync().ConfigureAwait(false);
                writer = null;
            }
        }
        catch (Exception exception)
        {
            Fault = exception;
            _ready.TrySetResult();
            _metrics.AddFault();
            _channel.Writer.TryComplete(exception);
        }
        finally
        {
            _ready.TrySetResult();
            if (writer is not null)
            {
                try
                {
                    await FlushAsync(Fault is null ? SessionLogFlushReason.Close : SessionLogFlushReason.Fault).ConfigureAwait(false);
                    await writer.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    Fault ??= exception;
                    _metrics.AddFault();
                }
            }

            drain.Stop();
        }

        async Task WriteBatchAsync(List<(FormattedLogRecord Record, int Bytes)> records)
        {
            StringBuilder pending = new();
            int pendingRecords = 0;
            int pendingBytes = 0;
            int indexOffset = 0;

            async Task CommitPendingAsync()
            {
                if (pendingRecords == 0)
                {
                    return;
                }

                await writer!.WriteAsync(pending.ToString()).ConfigureAwait(false);
                for (int index = 0; index < pendingRecords; index++)
                {
                    // Metrics preserve record granularity even though the disk write is batched.
                    _metrics.AddWrittenLogRecord(records[indexOffset + index].Bytes);
                }

                currentBytes += pendingBytes;
                indexOffset += pendingRecords;
                pending.Clear();
                pendingRecords = 0;
                pendingBytes = 0;
            }

            foreach ((FormattedLogRecord record, int recordBytes) in records)
            {
                if (writer is null ||
                    !record.BypassRotation &&
                    _options.RotationEnabled && currentBytes + pendingBytes > 0 && currentBytes + pendingBytes + recordBytes > _options.RotationBytes)
                {
                    await CommitPendingAsync().ConfigureAwait(false);
                    if (writer is not null)
                    {
                        await FlushAsync(SessionLogFlushReason.Rotation).ConfigureAwait(false);
                        await writer.DisposeAsync().ConfigureAwait(false);
                    }

                    string path = CreateSegmentPath(outputDirectory, segment++);
                    writer = new StreamWriter(
                        new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite, 64 * 1024, FileOptions.Asynchronous),
                        new UTF8Encoding(false),
                        64 * 1024)
                    {
                        AutoFlush = false,
                    };
                    Volatile.Write(ref _currentFilePath, path);
                    currentBytes = 0;
                }

                pending.Append(record.Text);
                pendingRecords++;
                pendingBytes += recordBytes;
            }

            await CommitPendingAsync().ConfigureAwait(false);
        }

        async Task FlushAsync(SessionLogFlushReason reason)
        {
            await writer!.FlushAsync().ConfigureAwait(false);
            _observer?.Flushed(reason);
        }
    }

    private string CreateSegmentPath(string outputDirectory, int segment)
    {
        string safeName = string.Concat(_options.SessionName.Select(character =>
            Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
        DateTimeOffset now = DateTimeOffset.Now;
        string baseName = _options.FileNameFormat
            .Replace("{Port}", safeName, StringComparison.Ordinal)
            .Replace("{yyyy}", now.ToString("yyyy", System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{MM}", now.ToString("MM", System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{dd}", now.ToString("dd", System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{HH}", now.ToString("HH", System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{mm}", now.ToString("mm", System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{ss}", now.ToString("ss", System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{fff}", now.ToString("fff", System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{yyyyMMdd}", now.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{HHmmss}", now.ToString("HHmmss", System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{Segment}", segment.ToString("D4", System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
        baseName = string.Concat(baseName.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));

        for (int collision = 0; ; collision++)
        {
            string suffix = collision == 0 ? string.Empty : $"-{collision:D2}";
            string path = Path.Combine(outputDirectory, $"{baseName}{suffix}.txt");
            if (!File.Exists(path))
            {
                return path;
            }
        }
    }
}
