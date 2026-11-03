using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DuCom.Plugins.Production;

public sealed class ProductionProfile
{
    public int SchemaVersion { get; set; } = 1;
    public string Name { get; set; } = "新项目";
    public string Revision { get; set; } = "1.0";
    public string Notes { get; set; } = "";
    public ProtocolSettings Protocol { get; set; } = new();
    public int LoopIntervalMs { get; set; } = 500;
    public int RemovalTimeoutMs { get; set; } = 100;
    public List<ProductionCommand> Commands { get; set; } = [];

    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true, PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);
    public static ProductionProfile FromJson(string json)
    {
        if (json.Length > 128 * 1024) throw new InvalidDataException("配置超过 128 KiB");
        ProductionProfile profile = JsonSerializer.Deserialize<ProductionProfile>(json, JsonOptions)
            ?? throw new InvalidDataException("配置为空");
        profile.Validate();
        return profile;
    }

    public void Validate()
    {
        if (SchemaVersion != 1) throw new InvalidDataException($"不支持的配置版本：{SchemaVersion}");
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 80) throw new InvalidDataException("项目名称需为 1–80 字符");
        if (Revision is null || Notes is null || Notes.Length > 2000) throw new InvalidDataException("项目备注无效或超过 2000 字符");
        if (Protocol is null || Commands is null || Commands.Count > 200) throw new InvalidDataException("配置不完整或命令超过 200 条");
        if (LoopIntervalMs is < 0 or > 60000) throw new InvalidDataException("循环间隔需为 0–60000 ms");
        if (RemovalTimeoutMs is < 50 or > 5000) throw new InvalidDataException("取走检测超时需为 50–5000 ms");
        Protocol.Validate();
        HashSet<string> ids = new(StringComparer.Ordinal);
        foreach (ProductionCommand c in Commands)
        {
            if (c is null || string.IsNullOrWhiteSpace(c.Id) || !ids.Add(c.Id)) throw new InvalidDataException("命令 ID 为空或重复");
            try
            {
                if (string.IsNullOrWhiteSpace(c.Name) || c.Name.Length > 80) throw new InvalidDataException("名称需为 1–80 字符");
                if (c.Steps.Count > 0)
                {
                    if (c.Steps.Count > 16 || c.Steps.Any(s => s.Steps.Count > 0)) throw new InvalidDataException("组合步骤最多 16 条，不允许嵌套");
                    if (c.DelayMs is < 0 or > 5000 || c.OutputStepId is null || c.SuccessText is null || c.SuccessText.Length > 200) throw new InvalidDataException("复合命令输出或间隔无效");
                    if (c.OutputStepId.Length > 0 && !c.Steps.Any(s => s.Id == c.OutputStepId && s.Enabled)) throw new InvalidDataException("输出步骤不存在或未启用");
                    if (!c.Steps.Any(s => s.Enabled)) throw new InvalidDataException("至少启用一个子命令");
                    new ProductionProfile { Name = c.Name, Protocol = c.Frame ?? Protocol, Commands = c.Steps }.Validate();
                    foreach (var rule in c.Comparisons)
                        if (!c.Steps.Any(s => s.Id == rule.ActualStepId && s.Enabled) || !c.Steps.Any(s => s.Id == rule.ExpectedStepId && s.Enabled)) throw new InvalidDataException("验证规则引用不存在或未启用的步骤");
                    continue;
                }
                if (!Enum.IsDefined(c.Channel)) throw new InvalidDataException("通道无效");
                if (c.DelayMs is < 0 or > 5000 || c.TimeoutMs is < 50 or > 30000) throw new InvalidDataException("延迟需为 0–5000 ms，超时需为 50–30000 ms");
                byte[] tx = Hex.Parse(c.Hex);
                if (tx.Length == 0 || tx.Length > 4096) throw new InvalidDataException("HEX 需为 1–4096 字节");
                if (c.MatchOffset < 0 || c.MatchOffset > 4096) throw new InvalidDataException("匹配偏移无效");
                Hex.Parse(c.ResponseMatch);
                if (c.ResponseSourceId is null || c.ResponseSourceOffset < 0 || c.ResponseSourceLength is < 0 or > 4096
                    || !Enum.IsDefined(c.ResponseSourceChannel) || c.ResponseSourceChannel == EarChannel.Both)
                    throw new InvalidDataException("应答数据来源无效");
                if (c.ResponseSourceId.Length > 0)
                {
                    int sourceIndex = Commands.FindIndex(x => x.Id == c.ResponseSourceId);
                    if (sourceIndex < 0 || sourceIndex >= Commands.IndexOf(c) || (c.Enabled && !Commands[sourceIndex].Enabled) || c.RawFrame || c.ResponseSourceLength < 1)
                        throw new InvalidDataException("数据来源必须是前序命令，且不能用于完整帧直发");
                }
                if (c.FixedResponseLength is < 0 or > 4096) throw new InvalidDataException("固定应答长度无效");
                if (c.StatusOffset < -1 || c.StatusOffset > 4095 || c.StatusValue is < 0 or > 255) throw new InvalidDataException("状态字段无效");
                if (c.Fields is null || c.Fields.Count > 16) throw new InvalidDataException("解析字段最多 16 个");
                foreach (ResponseField f in c.Fields)
                    if (f is null || f.Name is null || f.Unit is null || f.Name.Length > 80 || f.Unit.Length > 32 || !Enum.IsDefined(f.Type) || f.Offset < 0 || f.Offset > 4095 || f.Length is < 1 or > 256 || !double.IsFinite(f.Scale))
                        throw new InvalidDataException("解析字段无效");
                c.Frame?.Validate();
                if (c.TxLength is < -1 or > 255 || c.RxLength is < -1 or > 255) throw new InvalidDataException("长度需为自动或 0–255");
                ProtocolCodec.BuildFrame(c.Frame ?? Protocol, c);
            }
            catch (Exception e) when (e is FormatException or InvalidDataException or ArgumentException or OverflowException)
            { throw new InvalidDataException($"{c.Name}：{e.Message}", e); }
        }
    }

    public static ProductionProfile CreateExample() => new()
    {
        Name = "X8I", Revision = "1.1", Notes = "直接查询，不进入 DUT。获取两耳组队数据、交叉写入，再读回对耳 MAC。",
        Commands =
        [
            new() { Id = "mac", Name = "本机MAC", Hex = "94 01", Channel = EarChannel.Both, ResponseMatch = "94 02", Fields = [new() { Name = "MAC", Type = FieldType.Mac }] },
            new() { Id = "battery", Name = "电量", Hex = "97 01", Channel = EarChannel.Both, ResponseMatch = "97 02", Fields = [new() { Name = "电量", Type = FieldType.UInt8, Unit = "%" }] },
            new() { Id = "voltage", Name = "电压", Hex = "98 01", Channel = EarChannel.Both, ResponseMatch = "98 02", Fields = [new() { Name = "电压", Type = FieldType.UInt16, Unit = "mV" }] },
            new() { Id = "pair-left", Name = "获取左耳组队数据", Hex = "01 02", Channel = EarChannel.Left, ResponseMatch = "01", Enabled = false, PairingStep = true, DelayMs = 200 },
            new() { Id = "pair-right", Name = "获取右耳组队数据", Hex = "01 01", Channel = EarChannel.Right, ResponseMatch = "01", Enabled = false, PairingStep = true, DelayMs = 200 },
            new() { Id = "pair-write-left", Name = "写入右耳组队数据", Hex = "02", Channel = EarChannel.Left, ResponseMatch = "02", Enabled = false, PairingStep = true, DelayMs = 200, ResponseSourceId = "pair-right", ResponseSourceChannel = EarChannel.Right, ResponseSourceOffset = 4, ResponseSourceLength = 31 },
            new() { Id = "pair-write-right", Name = "写入左耳组队数据", Hex = "02", Channel = EarChannel.Right, ResponseMatch = "02", Enabled = false, PairingStep = true, DelayMs = 1000, ResponseSourceId = "pair-left", ResponseSourceChannel = EarChannel.Left, ResponseSourceOffset = 4, ResponseSourceLength = 31 },
            new() { Id = "peer-mac", Name = "对耳MAC", Hex = "96 01", Channel = EarChannel.Both, ResponseMatch = "96 02", PairingStep = true, Fields = [new() { Name = "MAC", Type = FieldType.Mac }] },
        ],
    };
}

public sealed class ProtocolSettings
{
    public int BaudRate { get; set; } = 9600;
    public string TxHeader { get; set; } = "55 AA";
    public string RxHeader { get; set; } = "55 AA";
    public int LengthOffset { get; set; } = 2;
    public int LengthSize { get; set; } = 1;
    public bool LengthLittleEndian { get; set; }
    // Total frame size = encoded length + LengthAdjustment.
    public int LengthAdjustment { get; set; } = 4;
    public int PayloadOffset { get; set; } = 3;
    public ChecksumKind Checksum { get; set; } = ChecksumKind.Crc8Maxim;
    public int CrcStart { get; set; }
    public int CrcEndTrim { get; set; }
    public bool CrcLittleEndian { get; set; }
    public void Validate()
    {
        if (BaudRate is < 300 or > 3000000) throw new InvalidDataException("波特率无效");
        if (Hex.Parse(TxHeader).Length is < 1 or > 16 || Hex.Parse(RxHeader).Length is < 1 or > 16) throw new InvalidDataException("帧头需为 1–16 字节");
        if (LengthOffset is < 0 or > 64 || LengthSize is < 1 or > 2 || PayloadOffset is < 1 or > 128
            || LengthAdjustment is < 0 or > 256 || CrcStart is < 0 or > 128 || CrcEndTrim is < 0 or > 128 || !Enum.IsDefined(Checksum))
            throw new InvalidDataException("帧字段配置无效");
        if (PayloadOffset < Hex.Parse(TxHeader).Length || LengthOffset < Hex.Parse(TxHeader).Length || LengthOffset + LengthSize > PayloadOffset)
            throw new InvalidDataException("发送帧头、长度字段与数据区重叠");
    }
}

public enum EarChannel { Left, Right, Both }
public enum ChecksumKind { None, Crc8Maxim, Crc8, Crc16Modbus, Crc16CcittFalse, Sum8, Xor8 }
public enum FieldType { Hex, Mac, UInt8, UInt16, UInt32, Int16, Int32, Ascii, Version }

public sealed class ProductionCommand
{
    public List<ResultComparison> Comparisons { get; set; } = [];
    public string OutputStepId { get; set; } = "";
    public string SuccessText { get; set; } = "执行完成";
    public List<ProductionCommand> Steps { get; set; } = [];
    public ProtocolSettings? Frame { get; set; }
    public int TxLength { get; set; } = -1;
    public int RxLength { get; set; } = -1;
    public bool PairingStep { get; set; }
    public string ResponseSourceId { get; set; } = "";
    public EarChannel ResponseSourceChannel { get; set; }
    public int ResponseSourceOffset { get; set; }
    public int ResponseSourceLength { get; set; }
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public bool Enabled { get; set; } = true;
    public string Name { get; set; } = "新命令";
    public string Hex { get; set; } = "94 01";
    public bool RawFrame { get; set; }
    public EarChannel Channel { get; set; }
    public int DelayMs { get; set; }
    public bool WaitResponse { get; set; } = true;
    public int TimeoutMs { get; set; } = 1000;
    public int FixedResponseLength { get; set; }
    public int MatchOffset { get; set; } = 3;
    public string ResponseMatch { get; set; } = "";
    public int StatusOffset { get; set; } = -1;
    public int StatusValue { get; set; }
    public List<ResponseField> Fields { get; set; } = [];
    public static ProductionCommand CreateX8IPairing() => new()
    {
        Id = "x8i-pairing", Name = "耳机组队", Enabled = false, Hex = "01", DelayMs = 1000,
        Steps =
        [
            new() { Id = "read-left", Name = "读取左耳组队数据", Channel = EarChannel.Left, PairingStep = true, Hex = "01 02", ResponseMatch = "01", FixedResponseLength = 36, DelayMs = 200 },
            new() { Id = "read-right", Name = "读取右耳组队数据", Channel = EarChannel.Right, PairingStep = true, Hex = "01 01", ResponseMatch = "01", FixedResponseLength = 36, DelayMs = 200 },
            new() { Id = "write-left", Name = "左耳写入右耳数据", Channel = EarChannel.Left, PairingStep = true, Hex = "02", ResponseMatch = "02", FixedResponseLength = 5, ResponseSourceId = "read-right", ResponseSourceChannel = EarChannel.Right, ResponseSourceOffset = 4, ResponseSourceLength = 31, DelayMs = 200 },
            new() { Id = "write-right", Name = "右耳写入左耳数据", Channel = EarChannel.Right, PairingStep = true, Hex = "02", ResponseMatch = "02", FixedResponseLength = 5, ResponseSourceId = "read-left", ResponseSourceChannel = EarChannel.Left, ResponseSourceOffset = 4, ResponseSourceLength = 31, DelayMs = 1000 },
        ],
    };
}

public sealed class ResultComparison
{
    public string ActualStepId { get; set; } = "";
    public string ExpectedStepId { get; set; } = "";
    public bool CompareOppositeChannel { get; set; } = true;
}

public sealed class ResponseField
{
    public bool ToEnd { get; set; }
    public string Example { get; set; } = "";
    public string Name { get; set; } = "值";
    public int Offset { get; set; } = 5;
    public FieldType Type { get; set; } = FieldType.Hex;
    public int Length { get; set; } = 1;
    public bool LittleEndian { get; set; }
    public bool Reverse { get; set; }
    public double Scale { get; set; } = 1;
    public string Unit { get; set; } = "";
}

public static class Hex
{
    public static byte[] Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string compact = string.Concat(text.Where(c => !char.IsWhiteSpace(c)));
        return Convert.FromHexString(compact);
    }
    public static string Format(byte[] data) => Convert.ToHexString(data).Chunk(2).Select(x => new string(x)).Aggregate(new StringBuilder(), (b, s) => b.Append(b.Length == 0 ? "" : " ").Append(s)).ToString();
}
