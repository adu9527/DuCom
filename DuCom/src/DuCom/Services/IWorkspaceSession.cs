using DuCom.Core.Ports;
using DuCom.Core.Logging;
using DuCom.Core.Sending;
using DuCom.Core.Sessions;
using DuCom.Core.Storage;

namespace DuCom.Services;

public interface IWorkspaceSession : IAsyncDisposable
{
    /// <summary>Stable identity of this session instance across close and reopen.</summary>
    string RuntimeId { get; }

    /// <summary>Generation of the currently open transport runtime, or null while closed.</summary>
    Guid? RuntimeGeneration { get; }

    SerialPortSettings Settings { get; }

    SerialSessionStatusSnapshot Status { get; }

    string LogDirectory { get; }

    string? CurrentLogFilePath { get; }

    Task<IReadOnlyList<SessionLogFileSnapshot>> CreateLogSnapshotAsync(CancellationToken cancellationToken = default);

    event EventHandler<SessionWarningEventArgs>? Warning;

    Task<PortCommandResult> OpenAsync(CancellationToken cancellationToken = default);

    Task<PortCommandResult> CloseAsync(CancellationToken cancellationToken = default);

    Task ApplySettingsAsync(SerialPortSettings settings, CancellationToken cancellationToken = default);

    ValueTask SendAsync(
        SendMode mode,
        string text,
        NewlinePolicy newline,
        CancellationToken cancellationToken = default);

    LineStoreSnapshot GetDisplaySnapshot(LineCursor? cursor, int maximumSegments);

    bool HasPendingDisplayDataOverLimit(LineCursor? cursor, int maximumSegments, int maximumCharacters);

    LineStoreSnapshot GetLatestDisplaySnapshot(int maximumSegments, int maximumCharacters);

    void ClearDisplay();

    void SetMemoryPressure(bool active);

    /// <summary>Display tap fan-out for auxiliary surfaces (float send window, log filter).</summary>
    SessionTapHub DisplayTaps { get; }

    /// <summary>Raw RX/TX traffic hub, including the legacy RX-only observer surface.</summary>
    SessionRawTapHub RawTaps { get; }
}
