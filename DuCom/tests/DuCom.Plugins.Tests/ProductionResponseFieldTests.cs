using System.Text.Json;
using DuCom.Plugins.Production;
using Xunit;

namespace DuCom.Plugins.Tests;

public sealed class ProductionResponseFieldTests
{
    [Theory]
    [InlineData(FieldType.Mac, 6)]
    [InlineData(FieldType.UInt8, 1)]
    [InlineData(FieldType.UInt16, 2)]
    [InlineData(FieldType.Int16, 2)]
    [InlineData(FieldType.UInt32, 4)]
    [InlineData(FieldType.Int32, 4)]
    public void FixedWidthsAgreeAcrossDefaultDisplayPreviewAndLegacyNormalization(FieldType type, int width)
    {
        ResponseField field = new() { Type = type };
        Assert.Equal(width, field.FixedLength);
        Assert.Equal(width, field.Length);
        Assert.Equal(width, field.EffectiveLength);
        Assert.Equal(width, CommandPreview.FieldSize(field));
        field.Length = 1;
        field.Validate(); // Direct callers may still hold old defaults.
        Assert.Equal(width, CommandPreview.FieldSize(field));
        field.Normalize();
        Assert.Equal(width, field.Length);
        string json = JsonSerializer.Serialize(field, ProductionProfile.JsonOptions);
        Assert.DoesNotContain("FixedLength", json, StringComparison.Ordinal);
        Assert.DoesNotContain("EffectiveLength", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{\"Length\":1,\"Type\":\"Mac\"}")]
    [InlineData("{\"Type\":\"Mac\",\"Length\":1}")]
    [InlineData("{\"Type\":\"Mac\"}")]
    public void LegacyMacImportsAndRoundTripsAtSixBytesRegardlessOfPropertyOrder(string fieldJson)
    {
        string json = "{\"Commands\":[{\"Fields\":[" + fieldJson + "]}]}";
        ProductionProfile profile = ProductionProfile.FromJson(json);
        ResponseField field = profile.Commands[0].Fields[0];
        Assert.Equal(6, field.Length);
        Assert.Equal("11:22:33:44:55:66", ProtocolCodec.Decode(profile.Commands[0], Hex.Parse("55 AA 08 94 02 11 22 33 44 55 66 00")));
        ProductionProfile copy = ProductionProfile.FromJson(profile.ToJson());
        Assert.Equal(6, copy.Commands[0].Fields[0].Length);
    }

    [Fact]
    public void ImportRecursesIntoCompoundStepsAndRetainsCustomFieldSettings()
    {
        const string json = """
            {"Commands":[{"Id":"group","Steps":[{"Id":"read","Fields":[
              {"Name":"mac","Type":"Mac","Length":1,"Offset":5,"Reverse":true,"Example":"11 22 33 44 55 66"},
              {"Name":"voltage","Type":"UInt16","Length":1,"Offset":11,"LittleEndian":true,"Scale":0.5,"Unit":"mV"},
              {"Name":"count","Type":"UInt32","Length":1,"Offset":13},
              {"Name":"bytes","Type":"Hex","Length":13,"Offset":17,"Reverse":true},
              {"Name":"text","Type":"Ascii","Length":27,"Offset":30,"ToEnd":true},
              {"Name":"version","Type":"Version","Length":3,"Offset":31}
            ]}]}]}
            """;
        ProductionProfile profile = ProductionProfile.FromJson(json);
        List<ResponseField> fields = profile.Commands[0].Steps[0].Fields;
        Assert.Equal(new[] { 6, 2, 4, 13, 27, 3 }, fields.Select(f => f.Length));
        Assert.Equal(new[] { 5, 11, 13, 17, 30, 31 }, fields.Select(f => f.Offset));
        Assert.True(fields[0].Reverse);
        Assert.Equal("11 22 33 44 55 66", fields[0].Example);
        Assert.True(fields[1].LittleEndian);
        Assert.Equal(0.5, fields[1].Scale);
        Assert.Equal("mV", fields[1].Unit);
        Assert.True(fields[3].Reverse);
        Assert.True(fields[4].ToEnd);
        Assert.Equal(profile.ToJson(), ProductionProfile.FromJson(profile.ToJson()).ToJson());
    }

    [Theory]
    [InlineData(FieldType.Hex)]
    [InlineData(FieldType.Ascii)]
    [InlineData(FieldType.Str)]
    [InlineData(FieldType.Version)]
    public void NormalizeNeverRewritesVariableWidths(FieldType type)
    {
        ResponseField field = new() { Type = type, Length = 19, ToEnd = true };
        field.Normalize();
        field.Validate();
        Assert.Null(field.FixedLength);
        Assert.Equal(19, field.Length);
        Assert.Equal(19, field.EffectiveLength);
        Assert.Equal(19, CommandPreview.FieldSize(field));
    }

    [Theory]
    [InlineData(FieldType.Mac, 5)]
    [InlineData(FieldType.UInt8, 2)]
    [InlineData(FieldType.UInt16, 3)]
    [InlineData(FieldType.Int16, 4)]
    [InlineData(FieldType.UInt32, 2)]
    [InlineData(FieldType.Int32, 6)]
    [InlineData(FieldType.Hex, 0)]
    [InlineData(FieldType.Ascii, 257)]
    [InlineData(FieldType.Str, 257)]
    public void InvalidLengthsAreRejectedWithoutSilentlyReplacingCustomValues(FieldType type, int length)
    {
        ResponseField field = new() { Type = type, Length = length };
        field.Normalize();
        Assert.Equal(length, field.Length);
        Assert.Throws<InvalidDataException>(field.Validate);
        Assert.Throws<InvalidDataException>(() => CommandPreview.FieldSize(field));
        ProductionProfile profile = new() { Commands = [new() { Fields = [field] }] };
        Assert.Throws<InvalidDataException>(profile.Validate);
        Assert.Throws<InvalidDataException>(() => ProductionProfile.FromJson(profile.ToJson()));
    }

    [Theory]
    [InlineData(FieldType.Mac)]
    [InlineData(FieldType.UInt8)]
    [InlineData(FieldType.UInt16)]
    [InlineData(FieldType.Int16)]
    [InlineData(FieldType.UInt32)]
    [InlineData(FieldType.Int32)]
    public void FixedTypesCannotUseToEnd(FieldType type)
    {
        ResponseField field = new() { Type = type, ToEnd = true };
        Assert.Throws<InvalidDataException>(field.Validate);
        Assert.Throws<InvalidDataException>(() => ProtocolCodec.Decode(new() { Fields = [field] }, new byte[16]));
        Assert.Throws<InvalidDataException>(() => new ProductionProfile { Commands = [new() { Fields = [field] }] }.Validate());
    }

    [Theory]
    [InlineData(ChecksumKind.None, "55 AA 00 01 02 11 22 33")]
    [InlineData(ChecksumKind.Crc8Maxim, "55 AA 00 01 02 11 22 33 FF")]
    [InlineData(ChecksumKind.Crc16Modbus, "55 AA 00 01 02 11 22 33 EE FF")]
    public void ToEndExcludesActualChecksumAndReverseDoesNotMutateFrame(ChecksumKind checksum, string bytes)
    {
        ProductionCommand command = new() { Fields = [new() { Type = FieldType.Hex, ToEnd = true, Reverse = true, Length = 23 }] };
        byte[] frame = Hex.Parse(bytes);
        byte[] original = (byte[])frame.Clone();
        Assert.Equal("33 22 11", ProtocolCodec.Decode(command, frame, new() { Checksum = checksum }));
        Assert.Equal(original, frame);
        Assert.Equal(23, command.Fields[0].Length);
        command.Frame = new() { Checksum = checksum };
        Assert.Equal("33 22 11", ProtocolCodec.Decode(command, frame));
    }

    [Theory]
    [InlineData(FieldType.Hex, "43 42 41")]
    [InlineData(FieldType.Ascii, "CBA")]
    [InlineData(FieldType.Str, "CBA")]
    [InlineData(FieldType.Version, "67.66.65")]
    public void VariableWidthTypesShareTheSameToEndContract(FieldType type, string expected)
    {
        ProductionCommand command = new() { Fields = [new() { Type = type, ToEnd = true, Reverse = true }] };
        Assert.Equal(expected, ProtocolCodec.Decode(command, Hex.Parse("55 AA 00 01 02 41 42 43 FF")));
    }

    [Fact]
    public void StrFieldsDecodeUtf8BluetoothNamesTrimPaddingAndFallBackToGbk()
    {
        // The first decode forces ProtocolCodec's static init, which registers the
        // code-pages provider needed for the GB18030 assertions below.
        ProductionCommand command = new()
        {
            Frame = new() { Checksum = ChecksumKind.None },
            Fields = [new() { Type = FieldType.Str, Offset = 5, ToEnd = true }],
        };
        // Standard layout: 55 AA <len> 94 02 <name…> — the name starts at offset 5.
        Assert.Equal("DuCom-X1", ProtocolCodec.Decode(command, [0x55, 0xAA, 0x08, 0x94, 0x02, 0x44, 0x75, 0x43, 0x6F, 0x6D, 0x2D, 0x58, 0x31]));
        // UTF-8 Chinese names decode directly, trailing NUL padding is trimmed.
        byte[] utf8 = [0x55, 0xAA, 0x0E, 0x94, 0x02, .. System.Text.Encoding.UTF8.GetBytes("蓝牙耳机"), 0x00, 0x00];
        Assert.Equal("蓝牙耳机", ProtocolCodec.Decode(command, utf8));
        // Legacy firmware may send GBK/GB18030: invalid as UTF-8, decoded via fallback.
        byte[] gbk = [0x55, 0xAA, 0x09, 0x94, 0x02, .. System.Text.Encoding.GetEncoding("GB18030").GetBytes("蓝牙耳机"), 0x00];
        Assert.Equal("蓝牙耳机", ProtocolCodec.Decode(command, gbk));
        // Empty and all-padding reads stay empty instead of throwing.
        Assert.Equal("", ProtocolCodec.Decode(command, [0x55, 0xAA, 0x00, 0x94, 0x02, 0x00, 0x00]));
    }

    [Theory]
    [InlineData(false, false, "4660")]
    [InlineData(false, true, "13330")]
    [InlineData(true, false, "13330")]
    [InlineData(true, true, "4660")]
    public void ReversePrecedesNumericByteOrder(bool reverse, bool littleEndian, string expected)
    {
        ProductionCommand command = new() { Fields = [new() { Type = FieldType.UInt16, Reverse = reverse, LittleEndian = littleEndian }] };
        Assert.Equal(expected, ProtocolCodec.Decode(command, Hex.Parse("55 AA 00 01 02 12 34 FF")));
    }

    [Fact]
    public void LegacyMacWidthAndReverseStillDecodeSixBytes()
    {
        ResponseField field = new() { Type = FieldType.Mac, Length = 1, Reverse = true };
        Assert.Equal("66:55:44:33:22:11", ProtocolCodec.Decode(new() { Fields = [field] }, Hex.Parse("55 AA 08 94 02 11 22 33 44 55 66 FF")));
        Assert.Equal(1, field.Length); // Reading does not mutate legacy objects.
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(4096, 1)]
    [InlineData(4095, 2)]
    [InlineData(int.MaxValue, 1)]
    public void InvalidOffsetOrExtentFailsAsDomainError(int offset, int length)
    {
        ResponseField field = new() { Offset = offset, Length = length };
        Assert.Throws<InvalidDataException>(field.Validate);
        Assert.Throws<InvalidDataException>(() => ProtocolCodec.Decode(new() { Fields = [field] }, new byte[8]));
        Assert.Throws<InvalidDataException>(() => new ProductionProfile { Commands = [new() { Fields = [field] }] }.Validate());
    }

    [Fact]
    public void FixedReadsCannotConsumeChecksumAndKnownResponseLengthsValidateFieldBounds()
    {
        ProductionCommand command = new() { Fields = [new() { Type = FieldType.Mac }] };
        Assert.Throws<InvalidDataException>(() => ProtocolCodec.Decode(command, Hex.Parse("55 AA 07 94 02 11 22 33 44 55 FF")));
        command.FixedResponseLength = 11;
        ProductionProfile profile = new() { Commands = [command] };
        Assert.Throws<InvalidDataException>(profile.Validate);
        command.FixedResponseLength = 12;
        profile.Validate();
        command.FixedResponseLength = 0;
        command.RxLength = 7; // Total 11 including CRC; one MAC byte is missing.
        Assert.Throws<InvalidDataException>(profile.Validate);
        command.RxLength = 8;
        profile.Validate();
        command.Frame = new() { Checksum = ChecksumKind.Crc16Modbus };
        Assert.Throws<InvalidDataException>(profile.Validate);
    }

    [Fact]
    public void ToEndMayReadEmptyDataButNotPastTheDataBoundary()
    {
        ResponseField field = new() { ToEnd = true };
        ProductionCommand command = new() { Fields = [field] };
        Assert.Equal("", ProtocolCodec.Decode(command, Hex.Parse("55 AA 00 01 02 FF")));
        field.Offset = 6;
        Assert.Throws<InvalidDataException>(() => ProtocolCodec.Decode(command, Hex.Parse("55 AA 00 01 02 FF")));
    }

    [Fact]
    public void PreviewRespectsNoncontiguousOffsetsPaddingAndStatusWithoutRearrangingFields()
    {
        ProductionCommand command = new()
        {
            ResponseMatch = "94 02", StatusOffset = 5, StatusValue = 0x7F,
            Fields = [new() { Name = "MAC", Type = FieldType.Mac, Offset = 8, Example = "11 22 33 44 55 66" }],
        };
        var preview = CommandPreview.Calculate(command, new());
        byte[] frame = Hex.Parse(preview.Rx);
        Assert.Equal("94 02 7F 00 00 11 22 33 44 55 66", Hex.Format(frame[3..^1]));
        Assert.Equal(8, command.Fields[0].Offset);
        Assert.Equal("11:22:33:44:55:66", ProtocolCodec.Decode(command, frame));
        Assert.True(ProtocolCodec.CheckChecksum(new(), frame));
        command.RxLength = 14;
        frame = Hex.Parse(CommandPreview.Calculate(command, new()).Rx);
        Assert.Equal(18, frame.Length);
        Assert.Equal(new byte[3], frame[14..17]);
    }

    [Fact]
    public void PreviewToEndRequiresSampleOrKnownResponseLengthAndUsesRealChecksumWidth()
    {
        ProductionCommand command = new() { ResponseMatch = "01 02", Fields = [new() { Type = FieldType.Hex, ToEnd = true }] };
        Assert.Throws<InvalidDataException>(() => CommandPreview.Calculate(command, new()));
        command.Fields[0].Example = "11 22 33";
        var preview = CommandPreview.Calculate(command, new() { Checksum = ChecksumKind.Crc16Modbus });
        Assert.Equal("11 22 33", ProtocolCodec.Decode(command, Hex.Parse(preview.Rx), new() { Checksum = ChecksumKind.Crc16Modbus }));
        command.Fields[0].Example = "";
        command.FixedResponseLength = 9;
        preview = CommandPreview.Calculate(command, new());
        Assert.Equal("00 00 00", ProtocolCodec.Decode(command, Hex.Parse(preview.Rx)));
        command.Fields[0].Example = "11 22";
        Assert.Throws<InvalidDataException>(() => CommandPreview.Calculate(command, new()));
    }

    [Fact]
    public void PreviewRejectsFieldCommandStatusHeaderAndChecksumOverlaps()
    {
        ProductionCommand command = new() { ResponseMatch = "94 02", Fields = [new() { Type = FieldType.Mac, Offset = 4 }] };
        Assert.Throws<InvalidDataException>(() => CommandPreview.Calculate(command, new()));
        command.Fields[0].Offset = 2;
        Assert.Throws<InvalidDataException>(() => CommandPreview.Calculate(command, new()));
        command.Fields[0].Offset = 5;
        command.Fields.Add(new() { Offset = 10 });
        Assert.Throws<InvalidDataException>(() => CommandPreview.Calculate(command, new()));
        command.Fields.RemoveAt(1);
        command.StatusOffset = 5;
        Assert.Throws<InvalidDataException>(() => CommandPreview.Calculate(command, new()));
        command.StatusOffset = -1;
        command.FixedResponseLength = 11;
        Assert.Throws<InvalidDataException>(() => CommandPreview.Calculate(command, new()));
    }

    [Fact]
    public void RawFrameProfilesRemainImportableButPreviewClearlyReportsUnsupportedLayout()
    {
        ProductionCommand command = new()
        {
            RawFrame = true, Hex = "0A 0B 01 BB 55 42 20 00 00 00 02", MatchOffset = 0,
            ResponseMatch = "0A 0B 01 BB 55 21 10 00 00 00 01", FixedResponseLength = 11,
            Frame = new() { TxHeader = "0A 0B", RxHeader = "0A 0B", Checksum = ChecksumKind.None },
        };
        ProductionProfile profile = new() { Commands = [command] };
        profile.Validate();
        ProductionProfile copy = ProductionProfile.FromJson(profile.ToJson());
        Assert.Equal(command.Hex, Hex.Format(ProtocolCodec.BuildFrame(copy.Commands[0].Frame!, copy.Commands[0])));
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => CommandPreview.Calculate(copy.Commands[0], copy.Protocol));
        Assert.Contains("完整帧直发", error.Message, StringComparison.Ordinal);
        copy.Validate();
    }

    [Fact]
    public void ExplicitArrangeStartsAfterActualMatchOffset()
    {
        ProductionCommand command = new() { MatchOffset = 5, ResponseMatch = "94 02", Fields = [new() { Type = FieldType.Mac }, new() { Type = FieldType.UInt16 }] };
        CommandPreview.ArrangeFields(command, new());
        Assert.Equal(7, command.Fields[0].Offset);
        Assert.Equal(13, command.Fields[1].Offset);
    }

    [Fact]
    public void DefaultExamplesAndBundledProfileDeclareRealFixedWidths()
    {
        ProductionProfile example = ProductionProfile.CreateExample();
        example.Validate();
        Assert.All(example.Commands.SelectMany(c => c.Fields), f => Assert.Equal(f.FixedLength, f.Length));
        // The embedded template is exactly what the shipped exe exports and offers in
        // the project list, so validate the resource, not a loose repo file.
        string json = Services.Plugins.ProductionBundledTemplate.LoadJson();
        ProductionProfile bundled = JsonSerializer.Deserialize<ProductionProfile>(json, ProductionProfile.JsonOptions)!;
        Assert.All(bundled.Commands.SelectMany(c => c.Fields).Where(f => f.FixedLength is not null), f => Assert.Equal(f.FixedLength, f.Length));
        bundled.Validate();
        Assert.Equal("55 AA 02 94 01 1C", Hex.Format(ProtocolCodec.BuildFrame(bundled.Protocol, bundled.Commands.Single(c => c.Id == "mac"))));
    }
}
