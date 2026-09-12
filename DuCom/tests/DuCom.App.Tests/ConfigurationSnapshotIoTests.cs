using System.IO;
using System.IO.Ports;
using DuCom.Core.Parsing;
using DuCom.Core.Sending;
using DuCom.Services;
using Xunit;

namespace DuCom.App.Tests;

public sealed class ConfigurationSnapshotIoTests
{
    [Fact]
    public void ExportImportRoundTripPreservesSnapshot()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ducom-settings-{Guid.NewGuid():N}.json");
        ConfigurationSnapshot expected = CreateSnapshot() with
        {
            TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff",
            CustomBaudRates = [9_600, 115_200, 3_000_000],
            HiddenPorts = ["COM7"],
            CommandTargetPortNames = ["COM2", "COM10"],
            PortOverrides = new(StringComparer.OrdinalIgnoreCase)
            {
                ["COM7"] = new(230_400, 7, StopBits.Two, Parity.Even, Handshake.RequestToSend, "utf-8"),
            },
        };

        try
        {
            ConfigurationSnapshotIo.Export(path, expected);

            Assert.True(ConfigurationSnapshotIo.TryImport(path, out ConfigurationSnapshot? actual, out IReadOnlyList<string> skipped));
            Assert.NotNull(actual);
            Assert.Empty(skipped);
            Assert.Equal(ConfigurationSnapshotIo.Serialize(expected), ConfigurationSnapshotIo.Serialize(actual));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void DeserializeIgnoresUnknownFields()
    {
        string json = ConfigurationSnapshotIo.Serialize(CreateSnapshot());
        json = json.Insert(json.LastIndexOf('}'), ",\n  \"FutureSetting\": 42");

        Assert.True(ConfigurationSnapshotIo.TryDeserialize(json, out ConfigurationSnapshot? snapshot, out IReadOnlyList<string> skipped));
        Assert.NotNull(snapshot);
        Assert.Empty(skipped);
        Assert.Equal(115_200, snapshot.BaudRate);
    }

    [Fact]
    public void DeserializeSkipsKnownCorruptedField()
    {
        string json = ConfigurationSnapshotIo.Serialize(CreateSnapshot())
            .Replace("\"BaudRate\": 115200", "\"BaudRate\": \"broken\"", StringComparison.Ordinal);

        Assert.True(ConfigurationSnapshotIo.TryDeserialize(json, out ConfigurationSnapshot? snapshot, out IReadOnlyList<string> skipped));
        Assert.NotNull(snapshot);
        Assert.Contains("BaudRate", skipped, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(0, snapshot.BaudRate);
        Assert.Equal(8, snapshot.DataBits);
    }

    private static ConfigurationSnapshot CreateSnapshot() => new(
        115_200,
        8,
        StopBits.One,
        Parity.None,
        Handshake.None,
        "utf-8",
        ReceiveDisplayMode.Str,
        true,
        true,
        "logs",
        SendMode.Str,
        NewlinePolicy.CrLf);
}
