namespace DuCom.Plugins.Production;

public static class CommandPreview
{
    public static int FieldSize(ResponseField f) => f.Type switch { FieldType.Mac => 6, FieldType.UInt8 => 1, FieldType.UInt16 or FieldType.Int16 => 2, FieldType.UInt32 or FieldType.Int32 => 4, _ => f.Length };
    public static void ArrangeFields(ProductionCommand c, ProtocolSettings project)
    {
        ProtocolSettings p = c.Frame ?? project;
        int offset = p.PayloadOffset + Hex.Parse(c.ResponseMatch).Length;
        foreach (ResponseField f in c.Fields) { f.Offset = offset; offset += FieldSize(f); }
    }
    public static (string Tx, string Rx) Calculate(ProductionCommand c, ProtocolSettings project)
    {
        ProtocolSettings p = c.Frame ?? project;
        byte[] tx = ProtocolCodec.BuildFrame(p, c);
        List<byte> payload = [.. Hex.Parse(c.ResponseMatch)];
        foreach (ResponseField f in c.Fields)
        {
            int size = FieldSize(f);
            byte[] sample = string.IsNullOrWhiteSpace(f.Example) ? new byte[size] : Hex.Parse(f.Example);
            if (sample.Length != size) throw new InvalidDataException($"{f.Name}：示例需要 {size} 字节");
            payload.AddRange(sample);
        }
        ProtocolSettings response = new() { TxHeader = p.RxHeader, RxHeader = p.RxHeader, LengthOffset = p.LengthOffset, LengthSize = p.LengthSize,
            LengthAdjustment = p.LengthAdjustment, PayloadOffset = p.PayloadOffset, Checksum = p.Checksum, CrcStart = p.CrcStart,
            CrcEndTrim = p.CrcEndTrim, CrcLittleEndian = p.CrcLittleEndian, LengthLittleEndian = p.LengthLittleEndian };
        byte[] rx = ProtocolCodec.BuildFrame(response, new() { Hex = Hex.Format(payload.ToArray()), TxLength = c.RxLength });
        return (Hex.Format(tx), Hex.Format(rx));
    }
}
