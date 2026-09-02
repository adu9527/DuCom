using DuCom.Core.Sending;
using Xunit;

namespace DuCom.Core.Tests.Sending;

public class CommandScriptSerializerTests
{
    [Fact]
    public void SerializeDeserialize_RoundTripPreservesEverything()
    {
        CommandGroup group = new(
            Guid.NewGuid(),
            "boot",
            [
                new ScriptCommand(Guid.NewGuid(), "reset", 1, "AT+RESET", false, 300, true, "OK", 2000, NewlinePolicy.CrLf),
                new ScriptCommand(Guid.NewGuid(), "ping", 0, "01 A0 FF", true, 50, false, string.Empty, 5_000, NewlinePolicy.None),
            ]);

        IReadOnlyList<CommandGroup> parsed = CommandScriptSerializer.Deserialize(CommandScriptSerializer.Serialize([group]), out IReadOnlyList<string> warnings);

        Assert.Empty(warnings);
        CommandGroup restored = Assert.Single(parsed);
        Assert.Equal(group.Name, restored.Name);
        Assert.Equal(2, restored.Commands.Count);
        ScriptCommand hex = restored.OrderedCommands()[0];
        Assert.True(hex.IsHex);
        Assert.Equal("01 A0 FF", hex.Payload);
        ScriptCommand check = restored.OrderedCommands()[1];
        Assert.True(check.IsResultCheck);
        Assert.Equal("OK", check.ExpectedResult);
        Assert.Equal(NewlinePolicy.CrLf, check.Newline);
        Assert.Equal(2_000, check.ResultTimeoutMilliseconds);
    }

    [Fact]
    public void Deserialize_InvalidJson_ReturnsEmptyWithWarning()
    {
        IReadOnlyList<CommandGroup> groups = CommandScriptSerializer.Deserialize("{ broken", out IReadOnlyList<string> warnings);

        Assert.Empty(groups);
        Assert.NotEmpty(warnings);
    }

    [Fact]
    public void Deserialize_FutureVersion_IsRejected()
    {
        string json = """{"version":99,"groups":[]}""";

        IReadOnlyList<CommandGroup> groups = CommandScriptSerializer.Deserialize(json, out _);

        Assert.Empty(groups);
    }

    [Fact]
    public void Deserialize_EmptyPayloadSkipped_BadIdsRegenerated()
    {
        string json = """
            {
              "version": 1,
              "groups": [
                {
                  "id": "not-a-guid",
                  "name": "g",
                  "commands": [
                    { "id": "x", "name": "empty", "payload": "" },
                    { "name": "valid", "payload": "AT" }
                  ]
                }
              ]
            }
            """;

        IReadOnlyList<CommandGroup> groups = CommandScriptSerializer.Deserialize(json, out IReadOnlyList<string> warnings);

        CommandGroup group = Assert.Single(groups);
        ScriptCommand command = Assert.Single(group.Commands);
        Assert.Equal("valid", command.Name);
        Assert.NotEqual(Guid.Empty, command.Id);
        Assert.NotEqual(Guid.Empty, group.Id);
        Assert.NotEmpty(warnings);
    }

    [Fact]
    public void Deserialize_ClampsUnreasonableValues()
    {
        string json = """
            {
              "version": 1,
              "groups": [
                { "name": "g", "commands": [
                  { "name": "c", "payload": "x", "delayMs": -5, "resultTimeoutMs": 999999999 }
                ] }
              ]
            }
            """;

        IReadOnlyList<CommandGroup> groups = CommandScriptSerializer.Deserialize(json, out _);

        ScriptCommand command = Assert.Single(Assert.Single(groups).Commands);
        Assert.Equal(0, command.DelayMilliseconds);
        Assert.Equal(3_600_000, command.ResultTimeoutMilliseconds);
    }
}

public class CommandGroupTests
{
    [Fact]
    public void OrderedCommands_SortsByOrderValue()
    {
        CommandGroup group = new(Guid.NewGuid(), "g", [
            new ScriptCommand(Guid.NewGuid(), "b", 2, "B", false, 0, false, "", 0, NewlinePolicy.None),
            new ScriptCommand(Guid.NewGuid(), "a", 1, "A", false, 0, false, "", 0, NewlinePolicy.None),
        ]);

        Assert.Equal(["a", "b"], group.OrderedCommands().Select(command => command.Name));
    }
}
