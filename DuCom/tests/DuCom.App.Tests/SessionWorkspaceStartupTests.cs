using System.Reflection;
using DuCom.Core.Parsing;
using DuCom.Core.Persistence;
using DuCom.Core.Ports;
using DuCom.Core.Sending;
using DuCom.Core.Sessions;
using DuCom.Services;
using DuCom.ViewModels;
using Xunit;

namespace DuCom.App.Tests;

public sealed class SessionWorkspaceStartupTests
{
    [Fact]
    public void DiscoveryNeverCreatesTabsAndRestoreOnlyOpensRememberedVisiblePorts() => WpfTestDispatcher.Run(() =>
    {
        List<string> created = [];
        HashSet<string> visible = new(StringComparer.OrdinalIgnoreCase) { "COM1", "COM3" };
        SessionWorkspaceViewModel workspace = CreateWorkspace(visible, created);
        workspace.SetPersistedState(new Dictionary<string, PortSettingSnapshot>(), [], ["COM1", "COM2"], ["COM1", "COM2"], "COM1", null);
        workspace.HandleDiscoveredPorts(["COM1", "COM2", "COM3"]);
        Assert.Empty(workspace.Sessions);
        workspace.RestorePersistedSessionsAsync().GetAwaiter().GetResult();
        Assert.Equal(["COM1"], created);
        Assert.Equal("COM1", Assert.Single(workspace.Sessions).PortName);
        workspace.HandleDiscoveredPorts(["COM1", "COM2", "COM3", "COM4"]);
        Assert.Single(workspace.Sessions);
        workspace.DisposeAsync().AsTask().GetAwaiter().GetResult();
    });

    [Fact]
    public void FilteredExplicitlyOpenedSessionSurvivesRestoreAndDiscovery() => WpfTestDispatcher.Run(() =>
    {
        List<string> created = [];
        HashSet<string> visible = [];
        SessionWorkspaceViewModel workspace = CreateWorkspace(visible, created);
        workspace.HandleDiscoveredPorts(["COM2"]);
        SessionViewModel opened = workspace.OpenPortAsync("COM2", false).GetAwaiter().GetResult()!;
        Assert.True(opened.IsOpen);
        workspace.SetPersistedState(new Dictionary<string, PortSettingSnapshot>(), [], [], ["COM2"], "COM2", null);
        workspace.RestorePersistedSessionsAsync().GetAwaiter().GetResult();
        workspace.HandleDiscoveredPorts(["COM2"]);
        Assert.Same(opened, Assert.Single(workspace.Sessions));
        Assert.True(opened.IsOpen);
        Assert.Equal(["COM2"], created);
        workspace.DisposeAsync().AsTask().GetAwaiter().GetResult();
    });

    private static SessionWorkspaceViewModel CreateWorkspace(HashSet<string> visible, List<string> created) => new(new SessionWorkspaceCallbacks(
        () => new ApplicationSessionDefaults(115200, 8, System.IO.Ports.StopBits.One, System.IO.Ports.Parity.None,
            System.IO.Ports.Handshake.None, "utf-8", ReceiveDisplayMode.Str, false, false, "", 10, false, 16, "{PortName}",
            false, "", "HH:mm:ss", SendMode.Str, NewlinePolicy.None, false),
        options =>
        {
            IWorkspaceSession session = DispatchProxy.Create<IWorkspaceSession, FakeSession>();
            ((FakeSession)session).Settings = options.PortSettings;
            created.Add(options.PortSettings.PortName);
            return session;
        },
        () => [], visible.Contains, _ => { }, () => Task.CompletedTask, () => Task.CompletedTask,
        () => { }, () => { }, () => { }, _ => false, () => { }, _ => { }, _ => { }, _ => { }, key => key,
        () => { }, () => { }, _ => Task.FromResult(true), () => { }));

    public class FakeSession : DispatchProxy
    {
        public SerialPortSettings Settings { get; set; } = SerialPortSettings.Default("COM1");
        private bool _open;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            switch (method?.Name)
            {
                case "get_Settings": return Settings;
                case "get_RuntimeId": return Settings.PortName;
                case "get_Status": return new SerialSessionStatusSnapshot(new PortLifecycleSnapshot(Settings.PortName,
                    _open ? PortLifecycleState.Open : PortLifecycleState.Closed, 0, DateTimeOffset.UnixEpoch, null), null!, null);
                case "OpenAsync": _open = true; return Task.FromResult(PortCommandResult.Succeeded);
                case "CloseAsync": _open = false; return Task.FromResult(PortCommandResult.Succeeded);
                case "DisposeAsync": return ValueTask.CompletedTask;
                case "add_Warning":
                case "remove_Warning": return null;
                default: throw new NotSupportedException(method?.Name);
            }
        }
    }
}
