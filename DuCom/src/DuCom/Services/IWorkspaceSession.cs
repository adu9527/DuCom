using DuCom.Core.Ports;
using DuCom.Core.Logging;
using DuCom.Core.Sending;
using DuCom.Core.Sessions;
using DuCom.Core.Storage;

namespace DuCom.Services;

public interface IWorkspaceSession : IAsyncDisposable
{
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

    void ClearDisplay();

    /// <summary>Display tap fan-out for auxiliary surfaces (float send window, log filter).</summary>
    SessionTapHub DisplayTaps { get; }
}
