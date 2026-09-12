using System.IO.Ports;
using System.Text;
using DuCom.Core.Parsing;
using DuCom.Core.Ports;
using DuCom.Core.Sending;
using DuCom.Services;
using DuCom.ViewModels;
using Xunit;

namespace DuCom.App.Tests;

public sealed class PortOverrideStoreTests
{
    [Fact]
    public void ApplyTransportUsesOverrideAndPreservesUnsetDeviceFlags()
    {
        PortOverrideStore store = new();
        store.Replace(new Dictionary<string, PortSettingSnapshot>(StringComparer.OrdinalIgnoreCase)
        {
            ["com7"] = new(921600, 7, StopBits.Two, Parity.Even, Handshake.RequestToSend,
                Encoding.ASCII.WebName, DtrEnable: true),
        });
        SerialPortSettings defaults = SerialPortSettings.Default("COM7") with
        {
            RtsEnable = true,
            DiscardNull = true,
        };

        SerialPortSettings result = store.ApplyTransport("COM7", defaults, CreateDefaults());

        Assert.Equal(921600, result.BaudRate);
        Assert.Equal(7, result.DataBits);
        Assert.Equal(StopBits.Two, result.StopBits);
        Assert.Equal(Parity.Even, result.Parity);
        Assert.True(result.DtrEnable);
        Assert.True(result.RtsEnable);
        Assert.True(result.DiscardNull);
    }

    [Fact]
    public void RememberRetainsExistingSessionPreferencesWhenOnlyTransportChanges()
    {
        PortOverrideStore store = new();
        Guid projectId = Guid.NewGuid();
        store.Replace(new Dictionary<string, PortSettingSnapshot>
        {
            ["COM9"] = new(115200, 8, StopBits.One, Parity.None, Handshake.None, Encoding.UTF8.WebName,
                SendMode: SendMode.Hex, Newline: NewlinePolicy.CrLf, ReceiveMode: ReceiveDisplayMode.Hex,
                AutoReconnect: true, HighlightRuleProjectId: projectId),
        });
        SerialPortSettings changed = SerialPortSettings.Default("COM9") with { BaudRate = 460800 };

        store.Remember("COM9", changed, null, CreateDefaults());
        Dictionary<string, PortSettingSnapshot> captured = store.Capture([], CreateDefaults());

        PortSettingSnapshot result = captured["COM9"];
        Assert.Equal(460800, result.BaudRate);
        Assert.Equal(SendMode.Hex, result.SendMode);
        Assert.Equal(NewlinePolicy.CrLf, result.Newline);
        Assert.Equal(ReceiveDisplayMode.Hex, result.ReceiveMode);
        Assert.True(result.AutoReconnect);
        Assert.Equal(projectId, result.HighlightRuleProjectId);
    }

    private static ApplicationSessionDefaults CreateDefaults() => new(
        115200, 8, StopBits.One, Parity.None, Handshake.None, Encoding.UTF8.WebName,
        ReceiveDisplayMode.Str, true, true, "Logs", 64, true, 64,
        "{Port}", true, "TX > ", "HH:mm:ss.fff", SendMode.Str, NewlinePolicy.None, false);
}
