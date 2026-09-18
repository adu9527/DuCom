using System.IO;
using DuCom.Core.Logging;
using DuCom.Core.Diagnostics;
using DuCom.Core.Parsing;
using DuCom.Core.Ports;
using DuCom.Core.Sending;
using DuCom.Core.Sessions;
using DuCom.Core.Storage;

namespace DuCom.Services;

internal sealed class SerialWorkspaceSession : IWorkspaceSession
{
    private readonly SerialSession _session;
    private readonly SerialWarningAggregator _warningAggregator;

    public SerialWorkspaceSession(
        SerialPortSettings settings,
        ReceiveDisplayMode receiveDisplayMode,
        bool timestampEnabled,
        bool loggingEnabled,
        string logDirectory,
        long logRotationBytes,
        bool logRotationEnabled,
        int displayBudgetBytes,
        string logFileNameFormat,
        bool sendPrefixEnabled,
        string sendPrefix,
        string timestampFormat,
        Func<long> memoryThresholdBytes)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(memoryThresholdBytes);
        SerialPortTransport transport = new(settings);
        _warningAggregator = new SerialWarningAggregator(memoryThresholdBytes, (warning, count, highMemory) =>
        {
            string countSuffix = count > 1 ? $" x{count}" : string.Empty;
            string memorySuffix = highMemory ? "; HighMemoryWarningAggregation=True" : string.Empty;
            Program.DiagnosticLog?.Warning($"Port warning. Port={settings.PortName}; {warning}{countSuffix}{memorySuffix}");
            Warning?.Invoke(this, new SessionWarningEventArgs($"{ResolveWarningText(warning)}{countSuffix}"));
        });
        transport.Warning += (_, warning) => _warningAggregator.Report(warning.Warning);
        _session = new SerialSession(
            transport,
            settings,
            receiveDisplayMode,
            timestampEnabled,
            new SessionLogWriterOptions(logDirectory, settings.PortName, logRotationBytes, Enabled: loggingEnabled, FileNameFormat: logFileNameFormat, RotationEnabled: logRotationEnabled, UseDateSubdirectory: true),
            displayBudgetBytes,
            sendPrefixEnabled,
            sendPrefix,
            timestampFormat,
            LogReceiveDiagnostic);
    }

    public string RuntimeId => _session.RuntimeId;

    public Guid? RuntimeGeneration => _session.RuntimeGeneration;

    public SerialPortSettings Settings => _session.Settings;

    public SerialSessionStatusSnapshot Status => _session.Status();

    public string LogDirectory => _session.LogDirectory;

    public string? CurrentLogFilePath => _session.CurrentLogFilePath;

    public Task<IReadOnlyList<SessionLogFileSnapshot>> CreateLogSnapshotAsync(CancellationToken cancellationToken = default) =>
        _session.CreateLogSnapshotAsync(cancellationToken);

    public event EventHandler<SessionWarningEventArgs>? Warning;

    private static void LogReceiveDiagnostic(ReceiveDiagnosticSnapshot snapshot) =>
        Program.DiagnosticLog?.Information(
            $"Receive burst. Port={snapshot.PortName}; Trigger={snapshot.Trigger}; " +
            $"CallbackGapMs={snapshot.CallbackGapMilliseconds:0.0}; InitialBytesAvailable={snapshot.InitialBytesAvailable}; " +
            $"ReadBlocks={snapshot.ReadBlocks}; ReadBytes={snapshot.ReadBytes}; MaxReadBytes={snapshot.MaximumReadBytes}; " +
            $"QueueDepthPeak={snapshot.QueueDepthPeak}; RemainingBytesAvailable={snapshot.RemainingBytesAvailable}; " +
            $"CapacityLimited={snapshot.CapacityLimited}; FormattedLines={snapshot.FormattedLines}; " +
            $"ProcessingMs={snapshot.ProcessingMilliseconds:0.0}");

    public Task<PortCommandResult> OpenAsync(CancellationToken cancellationToken = default) =>
        _session.OpenAsync(cancellationToken);

    public Task<PortCommandResult> CloseAsync(CancellationToken cancellationToken = default) =>
        _session.CloseAsync(cancellationToken);

    public Task ApplySettingsAsync(SerialPortSettings settings, CancellationToken cancellationToken = default) =>
        _session.ApplySettingsAsync(settings, cancellationToken);

    public ValueTask SendAsync(
        SendMode mode,
        string text,
        NewlinePolicy newline,
        CancellationToken cancellationToken = default) =>
        _session.SendAsync(mode, text, newline, cancellationToken);

    public LineStoreSnapshot GetDisplaySnapshot(LineCursor? cursor, int maximumSegments) =>
        _session.GetLinesAfter(cursor, maximumSegments);

    public bool HasPendingDisplayDataOverLimit(LineCursor? cursor, int maximumSegments, int maximumCharacters) =>
        _session.HasPendingLinesOverLimit(cursor, maximumSegments, maximumCharacters);

    public LineStoreSnapshot GetLatestDisplaySnapshot(int maximumSegments, int maximumCharacters) =>
        _session.GetLatestLines(maximumSegments, maximumCharacters);

    public void ClearDisplay() => _session.ClearDisplay();

    public void SetMemoryPressure(bool active) => _session.SetMemoryPressure(active);

    public SessionTapHub DisplayTaps => _session.DisplayTaps;

    public SessionRawTapHub RawTaps => _session.RawTaps;

    public async ValueTask DisposeAsync()
    {
        _warningAggregator.Dispose();
        await _session.DisposeAsync();
    }

    /// <summary>
    /// Maps the stable transport warning keys (for example <c>SerialWarning.Frame</c>) to
    /// localized text; unknown strings pass through unchanged.
    /// </summary>
    private static string ResolveWarningText(string warning)
    {
        string? localized = System.Windows.Application.Current?.TryFindResource(warning) as string;
        return localized ?? warning;
    }
}
