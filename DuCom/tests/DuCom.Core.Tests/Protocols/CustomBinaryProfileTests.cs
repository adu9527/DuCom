using DuCom.Core.Protocols.Custom;

namespace DuCom.Core.Tests.Protocols;

public sealed class CustomBinaryProfileTests
{
    [Fact]
    public void JsonLoaderLoadsValidProfilesAndHexMarkers()
    {
        const string Json = """
            {
              "schemaVersion": 1,
              "profiles": [{
                "id": "aa55",
                "name": "AA55",
                "maximumFrameLength": 64,
                "sync": { "bytes": "AA 55" },
                "fixedLength": 6,
                "fields": [{ "name": "value", "offset": 2, "type": "uint16", "byteOrder": "littleEndian" }]
              }]
            }
            """;

        CustomBinaryProfileLoadResult result = CustomBinaryProfileJson.Load(Json);

        CustomBinaryProfile profile = Assert.Single(result.Profiles);
        Assert.Equal([0xAA, 0x55], profile.Sync!.Bytes);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == DuCom.Core.Protocols.ProtocolDiagnosticSeverity.Error);
    }

    [Fact]
    public void JsonLoaderIsolatesInvalidProfileAndReportsJsonPaths()
    {
        const string Json = """
            {
              "schemaVersion": 1,
              "profiles": [
                { "id": "ok", "name": "OK", "fixedLength": 4 },
                { "id": "bad", "name": "Bad", "fixedLength": 0,
                  "fields": [{ "name": "x", "offset": -1, "type": "uint8" }] },
                { "id": "ok", "name": "Duplicate", "fixedLength": 4 }
              ]
            }
            """;

        CustomBinaryProfileLoadResult result = CustomBinaryProfileJson.Load(Json);

        Assert.Single(result.Profiles);
        Assert.Contains(result.Diagnostics, item => item.Path == "$.profiles[1].fixedLength");
        Assert.Contains(result.Diagnostics, item => item.Path == "$.profiles[1].fields[0].offset");
        Assert.Contains(result.Diagnostics, item => item.Path == "$.profiles[2].id" && item.Code == "custom.profile.duplicate-id");
    }

    [Fact]
    public void ValidatorEnforcesFramingFieldAndDepthLimits()
    {
        BinaryFieldDefinition nested = new() { Name = "level3", Offset = 0, Type = BinaryFieldType.UInt8 };
        nested = new BinaryFieldDefinition { Name = "level2", Offset = 0, Type = BinaryFieldType.UInt8, Children = [nested] };
        nested = new BinaryFieldDefinition { Name = "level1", Offset = 0, Type = BinaryFieldType.UInt8, Children = [nested] };
        CustomBinaryProfile profile = new()
        {
            Id = "invalid",
            Name = "Invalid",
            MaximumFrameLength = 8,
            FixedLength = 8,
            Terminator = new BinaryTerminatorDefinition { Bytes = [0x0A] },
            Fields = [nested],
        };

        CustomBinaryProfileValidationResult result = CustomBinaryProfileValidator.Validate(
            profile, new CustomBinaryProfileLimits(MaximumFields: 1, MaximumNestingDepth: 2));

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, item => item.Path == "$" && item.Message.StartsWith("Exactly one", StringComparison.Ordinal));
        Assert.Contains(result.Diagnostics, item => item.Path == "$.fields");
        Assert.Contains(result.Diagnostics, item => item.Path?.Contains("children", StringComparison.Ordinal) == true);
    }
}
