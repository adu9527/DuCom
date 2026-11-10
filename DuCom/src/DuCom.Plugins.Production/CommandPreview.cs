namespace DuCom.Plugins.Production;

public static class CommandPreview
{
    public static int FieldSize(ResponseField f)
    {
        f.Validate();
        return f.EffectiveLength;
    }
    public static void ArrangeFields(ProductionCommand c, ProtocolSettings project)
    {
        ProtocolSettings p = c.Frame ?? project;
        int offset = c.MatchOffset + Hex.Parse(c.ResponseMatch).Length;
        if (offset < p.PayloadOffset) throw new InvalidDataException("应答匹配与帧头或长度字段重叠");
        foreach (ResponseField f in c.Fields) { f.Offset = offset; offset += FieldSize(f); }
    }
    public static (string Tx, string Rx) Calculate(ProductionCommand c, ProtocolSettings project)
    {
        ProtocolSettings p = c.Frame ?? project;
        byte[] tx = ProtocolCodec.BuildFrame(p, c);
        if (c.RawFrame) throw new InvalidDataException("完整帧直发暂不支持自动构造应答预览；配置仍可保存，发送字节不变");
        p.Validate();
        int crcSize = ProtocolCodec.CrcSize(p.Checksum);
        int? declaredLength = c.FixedResponseLength > 0 ? c.FixedResponseLength
            : c.RxLength >= 0 ? c.RxLength + p.LengthAdjustment : null;
        byte[] match = Hex.Parse(c.ResponseMatch);
        if (c.MatchOffset is < 0 or > 4096 || match.Length > 4096 - c.MatchOffset)
            throw new InvalidDataException("应答匹配偏移无效");
        int dataEnd = Math.Max(p.PayloadOffset, c.MatchOffset + match.Length);
        if (c.StatusOffset < -1 || c.StatusOffset > 4095 || c.StatusValue is < 0 or > 255)
            throw new InvalidDataException("状态字段无效");
        if (c.StatusOffset >= 0) dataEnd = Math.Max(dataEnd, c.StatusOffset + 1);
        List<(ResponseField Field, byte[] Sample)> samples = [];
        foreach (ResponseField f in c.Fields)
        {
            f.Validate();
            byte[]? example = string.IsNullOrWhiteSpace(f.Example) ? null : Hex.Parse(f.Example);
            int size;
            if (f.ToEnd)
            {
                if (declaredLength is int length) size = f.GetReadLength(length, crcSize);
                else if (example is not null) size = example.Length;
                else throw new InvalidDataException($"{f.Name}：读到末尾需要示例字节或明确应答长度");
            }
            else size = f.EffectiveLength;
            if (example is not null && example.Length != size) throw new InvalidDataException($"{f.Name}：示例需要 {size} 字节");
            if (size < 0 || size > 4096 - f.Offset) throw new InvalidDataException($"{f.Name}：解析越界");
            samples.Add((f, example ?? new byte[size]));
            dataEnd = Math.Max(dataEnd, f.Offset + size);
        }
        int frameLength = declaredLength ?? dataEnd + crcSize;
        if (frameLength is < 1 or > 4096 || dataEnd > frameLength - crcSize)
            throw new InvalidDataException("应答字段超过应答长度");
        foreach (var (field, sample) in samples)
            if (field.GetReadLength(frameLength, crcSize) != sample.Length)
                throw new InvalidDataException($"{field.Name}：读到末尾示例未覆盖数据区末尾");
        int payloadLength = frameLength - crcSize - p.PayloadOffset;
        if (payloadLength < 0) throw new InvalidDataException("应答长度小于帧结构");
        ProtocolSettings response = new() { TxHeader = p.RxHeader, RxHeader = p.RxHeader, LengthOffset = p.LengthOffset, LengthSize = p.LengthSize,
            LengthAdjustment = p.LengthAdjustment, PayloadOffset = p.PayloadOffset, Checksum = p.Checksum, CrcStart = p.CrcStart,
            CrcEndTrim = p.CrcEndTrim, CrcLittleEndian = p.CrcLittleEndian, LengthLittleEndian = p.LengthLittleEndian };
        byte[] rx = ProtocolCodec.BuildFrame(response, new() { Hex = Hex.Format(new byte[payloadLength]), TxLength = c.RxLength });
        bool[] occupied = new bool[frameLength];
        Array.Fill(occupied, true, 0, p.PayloadOffset);
        Array.Fill(occupied, true, frameLength - crcSize, crcSize);
        void Place(int offset, byte[] bytes, string name)
        {
            if (offset < 0 || offset > rx.Length - bytes.Length) throw new InvalidDataException($"{name}：应答偏移越界");
            for (int i = 0; i < bytes.Length; i++)
                if (occupied[offset + i]) throw new InvalidDataException($"{name}：应答字段重叠");
            bytes.CopyTo(rx, offset);
            Array.Fill(occupied, true, offset, bytes.Length);
        }
        Place(c.MatchOffset, match, "应答匹配");
        if (c.StatusOffset >= 0) Place(c.StatusOffset, [(byte)c.StatusValue], "状态字段");
        foreach (var (field, sample) in samples) Place(field.Offset, sample, field.Name);
        ProtocolCodec.WriteChecksum(response, rx);
        return (Hex.Format(tx), Hex.Format(rx));
    }
}
