namespace DuCom.Plugin.Dto;

public sealed record ProductionRecordRequest
{
    public string Action { get; init; } = "append";
    public string Project { get; init; } = "";
    public string[] Logs { get; init; } = [];
    public string[] CsvRows { get; init; } = [];
}
