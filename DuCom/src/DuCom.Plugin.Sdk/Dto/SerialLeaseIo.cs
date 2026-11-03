namespace DuCom.Plugin.Dto;

public sealed record SerialLeaseIoRequest
{
    public string LeaseId { get; init; } = "";
    public int BaudRate { get; init; } = 9600;
    public int DataBits { get; init; } = 8;
    public string Parity { get; init; } = "None";
    public string StopBits { get; init; } = "One";
    public byte[] Bytes { get; init; } = [];
}
public sealed record SerialLeaseIoResult
{
    public string Path { get; init; } = "";
    public byte[] Bytes { get; init; } = [];
}
