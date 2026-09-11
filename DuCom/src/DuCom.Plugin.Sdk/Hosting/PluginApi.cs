using System.Text.Json;
using DuCom.Plugin.Dto;

namespace DuCom.Plugin;

public sealed class PluginHostException : Exception
{
    public PluginHostException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

public interface IPluginRequestChannel
{
    Task<JsonElement?> RequestAsync(string operation, object? request, CancellationToken cancellationToken);

    Task NotifyAsync(string operation, object? payload, CancellationToken cancellationToken);
}

public sealed record FilePickReadOptions(string Mode = "file", string? FilterName = null, string[]? Extensions = null, bool Remember = false)
{
    public static FilePickReadOptions File(params string[] extensions) => new("file", null, extensions);
    public static FilePickReadOptions Directory() => new("directory");
}

public sealed record FilePickWriteOptions(string Mode = "saveFile", string SuggestName = "output.bin", string? FilterName = null, string[]? Extensions = null)
{
    public static FilePickWriteOptions SaveFile(string suggestName) => new("saveFile", suggestName);
    public static FilePickWriteOptions Directory() => new("directory");
}

public sealed record FileReadChunk(byte[] Data, long TotalLength)
{
    public bool IsEnd => Data.Length == 0;
}

public sealed record LogSnapshotFileEntry(int Index, string Name, long Length, string Port);

public sealed record LogSnapshot(string Token, IReadOnlyList<LogSnapshotFileEntry> Files)
{
    public long TotalBytes => Files.Sum(file => file.Length);
}

public sealed record OutputHandle(string Token, long MaxBytes, int ChunkMaxBytes);
public sealed record PluginStorageSnapshot(string? Data, long Revision);
public sealed record PluginStorageCompareExchangeResult(bool Exchanged, string? Data, long Revision);
public sealed record OutputCommitStatus(string State, string? FinalPath, long Bytes, string? ErrorCode)
{
    public bool IsCommitted => State == "committed";
    public bool IsNotCommitted => State is "aborted" or "failed";
}

public interface IPluginStorage
{
    Task<string?> ReadAsync(CancellationToken cancellationToken = default);

    Task WriteAsync(string json, CancellationToken cancellationToken = default);

    Task<PluginStorageSnapshot> ReadVersionedAsync(CancellationToken cancellationToken = default);

    Task<PluginStorageCompareExchangeResult> CompareExchangeAsync(long expectedRevision, string json, CancellationToken cancellationToken = default);
}

public interface IPluginFiles
{
    Task<FilesPickResult?> PickReadAsync(FilePickReadOptions options, CancellationToken cancellationToken = default);

    Task<FilesPickResult?> PickWriteAsync(FilePickWriteOptions options, CancellationToken cancellationToken = default);

    /// <summary>Returns host-managed paths such as the application log directory (read-only information).</summary>
    Task<HostPathsResult> GetHostPathsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a write grant for a host-named file without any dialog: inside the host-managed log
    /// directory when <paramref name="directory"/> is null/empty, or inside a directory the user
    /// previously granted to this plugin. Collisions are resolved with numeric name suffixes.
    /// </summary>
    Task<FilesPickResult?> CreateWriteTargetAsync(string? directory, string suggestName, CancellationToken cancellationToken = default);

    Task<string?> RequestRememberedReadTokenAsync(string path, CancellationToken cancellationToken = default);

    Task ForgetRememberedReadPathAsync(string path, CancellationToken cancellationToken = default);

    Task<FilesStatResult> StatAsync(string token, CancellationToken cancellationToken = default);

    Task<FileReadChunk> ReadChunkAsync(string token, long offset, int length, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PickedEntry>> ListAsync(string token, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<FileSnapshotEntry>> CreateTaskSnapshotsAsync(string taskId, IReadOnlyList<string> tokens, CancellationToken cancellationToken = default);

}

public interface IPluginSerial : IDisposable
{
    event EventHandler<SerialDataEventArgs>? DataReceived;

    event EventHandler<string>? SessionClosed;

    Task<IReadOnlyList<SerialSessionInfo>> ListSessionsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SerialPortInfo>> ListPortsAsync(CancellationToken cancellationToken = default);

    Task<bool> SubscribeAsync(string sessionId, CancellationToken cancellationToken = default);

    Task UnsubscribeAllAsync(CancellationToken cancellationToken = default);
    Task<SerialLeaseResult> AcquireLeaseAsync(string taskId, string port, string? deviceIdentity, bool restoreSession, CancellationToken cancellationToken = default);
    Task<SerialLeaseResult> ReleaseLeaseAsync(string leaseId, CancellationToken cancellationToken = default);
}

public sealed class SerialDataEventArgs(byte[] data, string sessionId, long sequence, long byteOffset, DateTimeOffset receivedAtUtc) : EventArgs
{
    public byte[] Data { get; } = data;
    public string SessionId { get; } = sessionId;
    public long Sequence { get; } = sequence;
    public long ByteOffset { get; } = byteOffset;
    public DateTimeOffset ReceivedAtUtc { get; } = receivedAtUtc;
}

public interface IPluginLogs
{
    Task<IReadOnlyList<SerialSessionInfo>> ListSessionsAsync(CancellationToken cancellationToken = default);

    Task<LogSnapshot?> CreateSnapshotAsync(string? sessionId, CancellationToken cancellationToken = default);

    Task<FileReadChunk> ReadChunkAsync(string snapshotToken, int fileIndex, long offset, int length, CancellationToken cancellationToken = default);

    Task ReleaseSnapshotAsync(string snapshotToken, CancellationToken cancellationToken = default);
}

public interface IPluginOutput
{
    Task<OutputHandle> BeginAsync(CancellationToken cancellationToken = default);

    Task<long> WriteAsync(string outputToken, long offset, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);

    Task<FilesCommitResult> CommitAsync(string outputToken, string targetWriteToken, string commitId, CancellationToken cancellationToken = default);

    Task<OutputCommitStatus> GetCommitStatusAsync(string commitId, CancellationToken cancellationToken = default);

    Task DiscardAsync(string outputToken, CancellationToken cancellationToken = default);
}

public interface IPluginHelpers
{
    Task<HelperTaskResult> StartAsync(string helperId, string taskId, string payload, int timeoutMs, CancellationToken cancellationToken = default);
    Task<HelperTaskResult> GetStatusAsync(string taskId, CancellationToken cancellationToken = default);
    Task<HelperTaskResult> CancelAsync(string taskId, CancellationToken cancellationToken = default);
}

public interface IPluginUi
{
    Task UpdateToolPageAsync(string contributionId, IReadOnlyList<UiNode> nodes, CancellationToken cancellationToken = default);

    /// <summary>Shows a modal notice owned by the plugin's tool window; optionally reveals a file in Explorer.</summary>
    Task NotifyAsync(string title, string message, string? revealPath, CancellationToken cancellationToken = default);
}

public interface IPluginBackground
{
    Task SetAsync(string? imageToken, double? opacity, CancellationToken cancellationToken = default);

    Task ClearAsync(CancellationToken cancellationToken = default);
}

public interface IPluginDiagnostics
{
    void Info(string message);

    void Warning(string message);

    void LogError(string message);
}

public interface IPluginHostApi
{
    string PluginId { get; }

    string PluginVersion { get; }

    string Culture { get; }

    PluginLimits Limits { get; }

    IReadOnlyList<string> GrantedCapabilities { get; }

    IReadOnlyList<string> GrantedPermissions { get; }

    bool HasPermission(string permission);

    IPluginStorage Storage { get; }

    IPluginFiles Files { get; }

    IPluginSerial Serial { get; }

    IPluginLogs Logs { get; }

    IPluginOutput Output { get; }

    IPluginHelpers Helpers { get; }

    IPluginUi Ui { get; }

    IPluginBackground Background { get; }

    IPluginDiagnostics Diagnostics { get; }
}
