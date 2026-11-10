using System.Reflection;
using System.Windows;
using DuCom.Core.Parsing;
using DuCom.Core.Ports;
using DuCom.Core.Sessions;
using DuCom.Core.Storage;
using DuCom.Services;
using DuCom.ViewModels;
using Xunit;

namespace DuCom.App.Tests;

public sealed class DisplayProjectionSchedulingTests
{
    [Fact]
    public void InactiveWindowRefreshesStateWithoutReadingDisplayAndCatchesUpWhenActive() => WpfTestDispatcher.Run(() =>
    {
        IWorkspaceSession transport = DispatchProxy.Create<IWorkspaceSession, CountingSession>();
        CountingSession source = (CountingSession)transport;
        SessionViewModel session = new(transport, ReceiveDisplayMode.Str, false, true);
        try
        {
            // RX/logging belong to the session, independent of whether the UI projects it.
            source.AddLines(128);
            for (int i = 0; i < 85; i++) MainViewModel.RefreshSessionDisplay(session, false, TimeSpan.FromMilliseconds(8));
            Assert.Equal(0, source.DisplayReads);
            Assert.Equal(128, source.ReceivedLines);
            Assert.Equal(128, source.LoggedLines);
            Assert.Empty(session.VisibleLines);
            MainViewModel.RefreshSessionDisplay(session, true, TimeSpan.FromSeconds(1));
            Assert.Equal(128, session.VisibleLines.Count);
            Assert.Equal(1, source.DisplayReads);
            source.Faulted = true;
            Assert.True(session.RefreshStatus());
            Assert.True(session.HasFault);
            Assert.False(session.IsOpen);
            Assert.Equal(1, source.DisplayReads);
        }
        finally { session.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    });

    public class CountingSession : DispatchProxy
    {
        public int DisplayReads { get; private set; }
        public int ReceivedLines { get; private set; }
        public int LoggedLines { get; private set; }
        public bool Faulted { get; set; }
        private readonly List<StoredLine> _lines = [];
        private readonly SessionTapHub _display = new();
        private readonly SessionRawTapHub _raw = new();
        public void AddLines(int count)
        {
            for (int i = 0; i < count; i++)
            {
                ReceivedLines++; LoggedLines++;
                _lines.Add(new StoredLine(ReceivedLines, 0, LineDirection.Rx, DateTimeOffset.UtcNow, "line " + ReceivedLines, true));
            }
        }
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            switch (method!.Name)
            {
                case "get_Settings": return SerialPortSettings.Default("COM123");
                case "get_RuntimeId": return "fixture";
                case "get_RuntimeGeneration": return (Guid?)null;
                case "get_Status": return new SerialSessionStatusSnapshot(new PortLifecycleSnapshot("COM123",
                    Faulted ? PortLifecycleState.Closed : PortLifecycleState.Open, 1, DateTimeOffset.UtcNow, null), null!,
                    Faulted ? new SessionFaultSnapshot("Fixture", "disconnected") : null);
                case "get_DisplayTaps": return _display;
                case "get_RawTaps": return _raw;
                case "get_LogDirectory": return "fixture-not-written";
                case "get_CurrentLogFilePath": return null;
                case "GetDisplaySnapshot":
                    DisplayReads++;
                    LineCursor? cursor = (LineCursor?)args![0];
                    return new LineStoreSnapshot(_lines.Count == 0 ? null : 1, _lines.Count, 0,
                        _lines.Where(line => cursor is null || line.LogicalId > cursor.Value.LogicalId).Take((int)args[1]!).ToArray());
                case "HasPendingDisplayDataOverLimit": return false;
                case "DisposeAsync": return ValueTask.CompletedTask;
                case "add_Warning": case "remove_Warning": return null;
                default: throw new NotSupportedException(method.Name);
            }
        }
    }
}
