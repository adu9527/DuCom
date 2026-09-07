using System.Text.Json;
using System.Text.Json.Serialization;
using DuCom.Core.Persistence;

namespace DuCom.Core.Sending;

/// <summary>Single scripted command item inside a command group.</summary>
public sealed record ScriptCommand(
    Guid Id,
    string Name,
    int Order,
    string Payload,
    bool IsHex,
    int DelayMilliseconds,
    bool IsResultCheck,
    string ExpectedResult,
    int ResultTimeoutMilliseconds,
    NewlinePolicy Newline)
{
    public const int DefaultDelayMilliseconds = 200;

    public const int DefaultResultTimeoutMilliseconds = 5_000;

    public static ScriptCommand Create(string name = "", string payload = "", int order = 0) => new(
        Guid.NewGuid(),
        name,
        order,
        payload,
        IsHex: false,
        DelayMilliseconds: DefaultDelayMilliseconds,
        IsResultCheck: false,
        ExpectedResult: string.Empty,
        ResultTimeoutMilliseconds: DefaultResultTimeoutMilliseconds,
        Newline: NewlinePolicy.None);
}

/// <summary>
/// A flat command group ("project"): an ordered list of script commands. Deliberately
/// one level only, matching the reference workflow; repeats are expressed by looping the
/// whole group until stopped rather than a per-command count.
/// </summary>
public sealed record CommandGroup(
    Guid Id,
    string Name,
    IReadOnlyList<ScriptCommand> Commands)
{
    public static CommandGroup Create(string name) =>
        new(Guid.NewGuid(), name, []);

    /// <summary>Returns commands sorted ascending by explicit order value.</summary>
    public IReadOnlyList<ScriptCommand> OrderedCommands() =>
        [.. Commands.OrderBy(command => command.Order).ThenBy(command => command.Name, StringComparer.OrdinalIgnoreCase)];
}

/// <summary>Versioned persistence envelope for all command groups plus single-group import/export.</summary>
public sealed record CommandGroupDocument
{
    public const int CurrentVersion = 1;

    [JsonPropertyName("version")]
    public int Version { get; init; } = CurrentVersion;

    [JsonPropertyName("groups")]
    public List<CommandGroupDto> Groups { get; init; } = [];
}

public sealed record CommandGroupDto
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("commands")]
    public List<ScriptCommandDto> Commands { get; init; } = [];
}

public sealed record ScriptCommandDto
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("order")]
    public int Order { get; init; }

    [JsonPropertyName("payload")]
    public string Payload { get; init; } = string.Empty;

    [JsonPropertyName("hex")]
    public bool Hex { get; init; }

    [JsonPropertyName("delayMs")]
    public int DelayMs { get; init; } = ScriptCommand.DefaultDelayMilliseconds;

    [JsonPropertyName("checkResult")]
    public bool CheckResult { get; init; }

    [JsonPropertyName("expectedResult")]
    public string ExpectedResult { get; init; } = string.Empty;

    [JsonPropertyName("resultTimeoutMs")]
    public int ResultTimeoutMs { get; init; } = ScriptCommand.DefaultResultTimeoutMilliseconds;

    [JsonPropertyName("newline")]
    public string? Newline { get; init; }
}

public static class CommandScriptSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public static string Serialize(IEnumerable<CommandGroup> groups)
    {
        ArgumentNullException.ThrowIfNull(groups);
        var collection = new CommandGroupDocument();
        foreach (CommandGroup group in groups)
        {
            collection.Groups.Add(new CommandGroupDto
            {
                Id = group.Id.ToString(),
                Name = group.Name,
                Commands = [.. group.Commands.Select(ToDto)],
            });
        }

        return JsonSerializer.Serialize(collection, Options);
    }

    /// <summary>
    /// Parses groups. Unparsable content degrades to warnings; individual bad groups or
    /// commands are skipped or defaulted instead of failing the whole file.
    /// </summary>
    public static IReadOnlyList<CommandGroup> Deserialize(string json, out IReadOnlyList<string> warnings)
    {
        List<string> warningList = [];
        CommandGroupDocument? collection;
        if (!TolerantJsonLoader.TryLoad<CommandGroupDocument>(json, Options, out CommandGroupDocument? tolerant, out IReadOnlyList<string> skipped))
        {
            warningList.Add("document is not readable JSON");
            warnings = warningList;
            return [];
        }

        if (skipped.Count > 0)
        {
            warningList.Add($"unreadable section(s) skipped: {string.Join(", ", skipped)}");
        }

        collection = tolerant;

        List<CommandGroup> groups = [];
        if (collection is null || !VersionSupported(collection.Version))
        {
            warnings = warningList;
            return groups;
        }

        foreach (CommandGroupDto dto in collection.Groups)
        {
            if (!Guid.TryParse(dto.Id, out Guid id))
            {
                id = Guid.NewGuid();
                warningList.Add($"group '{dto.Name}': missing id, regenerated");
            }

            List<ScriptCommand> commands = [];
            foreach (ScriptCommandDto commandDto in dto.Commands)
            {
                if (!Guid.TryParse(commandDto.Id, out Guid commandId))
                {
                    commandId = Guid.NewGuid();
                }

                // Empty payloads are valid in-progress editor rows; dropping them here
                // made freshly added commands vanish on the next load.
                commands.Add(new ScriptCommand(
                    commandId,
                    commandDto.Name,
                    commandDto.Order,
                    commandDto.Payload,
                    commandDto.Hex,
                    Clamp(commandDto.DelayMs, 0, 3_600_000),
                    commandDto.CheckResult,
                    commandDto.ExpectedResult,
                    Clamp(Math.Max(commandDto.ResultTimeoutMs, 1), 1, 3_600_000),
                    ParseNewline(commandDto.Newline)));
            }

            groups.Add(new CommandGroup(id, dto.Name, commands));
        }

        warnings = warningList;
        return groups;
    }

    private static ScriptCommandDto ToDto(ScriptCommand command) => new()
    {
        Id = command.Id.ToString(),
        Name = command.Name,
        Order = command.Order,
        Payload = command.Payload,
        Hex = command.IsHex,
        DelayMs = command.DelayMilliseconds,
        CheckResult = command.IsResultCheck,
        ExpectedResult = command.ExpectedResult,
        ResultTimeoutMs = command.ResultTimeoutMilliseconds,
        Newline = command.Newline.ToString(),
    };

    private static bool VersionSupported(int version) => version is > 0 and <= CommandGroupDocument.CurrentVersion;

    private static int Clamp(int value, int minimum, int maximum) => Math.Clamp(value, minimum, maximum);

    private static NewlinePolicy ParseNewline(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "cr" => NewlinePolicy.Cr,
        "lf" => NewlinePolicy.Lf,
        "crlf" => NewlinePolicy.CrLf,
        _ => NewlinePolicy.None,
    };
}

