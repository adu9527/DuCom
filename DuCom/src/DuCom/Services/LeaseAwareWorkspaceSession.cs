using DuCom.Core.Logging;
using DuCom.Core.Ports;
using DuCom.Core.Sending;
using DuCom.Core.Sessions;
using DuCom.Core.Storage;
using DuCom.PluginHost.Core;

namespace DuCom.Services;

internal sealed class LeaseAwareWorkspaceSession(IWorkspaceSession inner, SerialLeaseCoordinator leases) : IWorkspaceSession
{
    public string RuntimeId => inner.RuntimeId;
    public SerialPortSettings Settings => inner.Settings;
    public SerialSessionStatusSnapshot Status => inner.Status;
    public string LogDirectory => inner.LogDirectory;
    public string? CurrentLogFilePath => inner.CurrentLogFilePath;
    public event EventHandler<SessionWarningEventArgs>? Warning { add => inner.Warning += value; remove => inner.Warning -= value; }
    public SessionTapHub DisplayTaps => inner.DisplayTaps;
    public SessionRawTapHub RawTaps => inner.RawTaps;
    public Task<PortCommandResult> OpenAsync(CancellationToken cancellationToken = default) => leases.RunPortOperationAsync(Settings.PortName, async () => { Demand(); return await inner.OpenAsync(cancellationToken); });
    public Task<PortCommandResult> CloseAsync(CancellationToken cancellationToken = default) => inner.CloseAsync(cancellationToken);
    public Task ApplySettingsAsync(SerialPortSettings settings, CancellationToken cancellationToken = default) => leases.RunPortOperationAsync(Settings.PortName, async () => { Demand(); await inner.ApplySettingsAsync(settings, cancellationToken); return true; });
    public ValueTask SendAsync(SendMode mode, string text, NewlinePolicy newline, CancellationToken cancellationToken = default) => new(leases.RunPortOperationAsync(Settings.PortName, async () => { Demand(); await inner.SendAsync(mode, text, newline, cancellationToken); return true; }));
    public LineStoreSnapshot GetDisplaySnapshot(LineCursor? after, int maxCount) => inner.GetDisplaySnapshot(after, maxCount);
    public void ClearDisplay() => inner.ClearDisplay();
    public Task<IReadOnlyList<SessionLogFileSnapshot>> CreateLogSnapshotAsync(CancellationToken cancellationToken = default) => inner.CreateLogSnapshotAsync(cancellationToken);
    public ValueTask DisposeAsync() => inner.DisposeAsync();
    private void Demand() { if (!leases.CanUse(Settings.PortName)) throw new InvalidOperationException($"Serial port '{Settings.PortName}' is leased by a plugin task."); }
}
