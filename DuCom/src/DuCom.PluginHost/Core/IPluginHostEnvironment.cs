using DuCom.Plugin;

namespace DuCom.PluginHost;

public sealed record HostSerialSession(string SessionId, string Port, bool Open);
public sealed record HostSerialPort(string Port, string DisplayName, string VidPid, string DeviceIdentity);
public sealed record HostSerialLeaseRequest(string PluginId, string ActivationId, string TaskId, string Port, string? DeviceIdentity, bool RestoreSession);
public sealed record HostSerialLeaseResult(string LeaseId, string Port, string State, bool SessionWasOpen, bool Restored, string? Message);

public sealed record HostLogSnapshotFile(string Path, long Length, string Port, string DisplayName);

public sealed record HostLogSnapshot(string SessionId, string Port, IReadOnlyList<HostLogSnapshotFile> Files);

public sealed record HostPickEntry(string Name, bool IsDirectory, long Length);

public sealed record HostPickResult
{
    public required string DisplayPath { get; init; }
    public required bool IsDirectory { get; init; }
    public required bool Remembered { get; init; }
    public IReadOnlyList<HostPickEntry> Entries { get; init; } = [];
    /// <summary>True only when the user explicitly approved replacing this path.</summary>
    public bool ReplacePathApproved { get; init; }
}

public sealed record HostPickRequest
{
    public required string Mode { get; init; }
    public string? FilterName { get; init; }
    public IReadOnlyList<string> Extensions { get; init; } = [];
    public string? SuggestName { get; init; }
    public bool Remember { get; init; }
}

public sealed record HostFaultNotice
{
    public required string PluginId { get; init; }
    public required string PluginName { get; init; }
    public required string Version { get; init; }
    public required string Reason { get; init; }
    public required string ActivationId { get; init; }
    public required bool ExitConfirmed { get; init; }
    public required bool BudgetProtective { get; init; }
    public string Kind => BudgetProtective ? "budget" : "fault";
}

public sealed record PluginPublishedActivation
{
    public required string PluginId { get; init; }
    public required string PluginName { get; init; }
    public required string Version { get; init; }
    public required string ActivationId { get; init; }
    public required IReadOnlyList<MenuContribution> Menus { get; init; }
    public required IReadOnlyList<SettingsPanelContribution> SettingsPanels { get; init; }
    public required IReadOnlyList<ToolPageContribution> ToolPages { get; init; }
    public required IReadOnlyList<BackgroundImageContribution> Backgrounds { get; init; }
}

public sealed record BackgroundApply
{
    public static BackgroundApply Clear { get; } = new();

    public string? PluginId { get; init; }
    public string? ImageTokenPath { get; init; }
    public double? Opacity { get; init; }
}

public sealed record HostPluginNotice
{
    public required string PluginId { get; init; }
    public required string Title { get; init; }
    public required string Message { get; init; }
    /// <summary>When set, the host reveals this file in Windows Explorer after the notice is dismissed.</summary>
    public string? RevealPath { get; init; }
}

/// <summary>
/// Environment services the host application provides to the plugin runtime. Implemented on
/// the application side: serial session enumeration and raw receive taps, log snapshots via
/// the existing session snapshot barrier, user-driven file pickers, declarative UI publication,
/// background rendering, and fault notices. All members may be called from worker threads.
/// </summary>
public interface IPluginHostEnvironment
{
    string HostVersion { get; }

    string Culture { get; }

    IReadOnlyList<HostSerialSession> GetSerialSessions();
    IReadOnlyList<HostSerialPort> GetSerialPorts();

    Task<IReadOnlyList<HostLogSnapshot>> CreateLogSnapshotsAsync(string? sessionId, CancellationToken cancellationToken);

    IDisposable SubscribeRawBlocks(Action<string, ReadOnlyMemory<byte>, DateTimeOffset> handler);

    event EventHandler<string>? SessionClosed;

    Task<HostSerialLeaseResult> AcquireSerialLeaseAsync(HostSerialLeaseRequest request, CancellationToken cancellationToken);

    Task<HostSerialLeaseResult> ReleaseSerialLeaseAsync(string pluginId, string activationId, string leaseId, CancellationToken cancellationToken);

    void RevokeSerialLeases(string pluginId, string activationId);

    Task<HostPickResult?> PickReadAsync(string pluginId, HostPickRequest request, CancellationToken cancellationToken);

    Task<HostPickResult?> PickWriteAsync(string pluginId, HostPickRequest request, CancellationToken cancellationToken);

    /// <summary>The application's current log directory (the built-in default lives beside the executable).</summary>
    string GetLogDirectory();

    /// <summary>Returns true only after the notice is displayed, never merely queued. Failure or shutdown leaves it pending.</summary>
    Task<bool> ShowNoticeAsync(HostPluginNotice notice);

    string? TryResolveRememberedReadPath(string pluginId, string requestedPath);

    /// <summary>Returns true only after the notice is displayed, never merely queued. Failure or shutdown leaves it pending.</summary>
    Task<bool> ShowFaultNoticeAsync(HostFaultNotice notice);

    void PublishActivation(PluginPublishedActivation activation);

    void UpdateToolPage(string pluginId, string contributionId, IReadOnlyList<UiNode> nodes);

    void RemoveActivation(string pluginId);

    void ApplyBackground(BackgroundApply apply);
}
