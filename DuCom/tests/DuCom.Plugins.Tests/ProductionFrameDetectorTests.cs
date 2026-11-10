using System.IO.Compression;
using DuCom.Plugins.Production;
using DuCom.Services.Plugins;
using Xunit;

namespace DuCom.Plugins.Tests;

public sealed class ProductionFrameDetectorTests
{
    [Fact]
    public void DetectsCanonicalX8ISendFrameStructure()
    {
        byte[] frame = ProtocolCodec.BuildFrame(new(), new() { Hex = "94 01" });
        Assert.Equal("55 AA 02 94 01 1C", Hex.Format(frame));
        List<ProductionFrameCandidate> candidates = ProductionFrameDetector.Detect(frame);
        Assert.NotEmpty(candidates);
        ProductionFrameCandidate best = candidates[0];
        Assert.Equal(2, best.HeaderLength);
        Assert.Equal(ChecksumKind.Crc8Maxim, best.Crc);
        Assert.Equal(2, best.LengthValue);
        Assert.Equal(4, best.LengthAdjustment);
        Assert.Equal(3, best.PayloadOffset);
        Assert.True(best.Canonical);
    }

    [Fact]
    public void PairDetectionIntersectsSendAndReplyStructures()
    {
        byte[] send = ProtocolCodec.BuildFrame(new(), new() { Hex = "94 01" });
        List<byte> reply = [0x55, 0xAA, 0x08, 0x94, 0x02, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x00];
        ProtocolSettings replySettings = new() { LengthAdjustment = 4 };
        byte[] replyFrame = [.. reply];
        ProtocolCodec.WriteChecksum(replySettings, replyFrame);
        List<ProductionFrameCandidate> common = ProductionFrameDetector.DetectPair(send, replyFrame);
        Assert.NotEmpty(common);
        Assert.All(common, candidate => Assert.Equal(2, candidate.HeaderLength));
    }

    [Fact]
    public void NoiseOrTruncatedFramesYieldNoCandidates()
    {
        Assert.Empty(ProductionFrameDetector.Detect([0x01, 0x02, 0x03]));
        Assert.Empty(ProductionFrameDetector.Detect(new byte[12].ToArray()));
    }

    [Fact]
    public void ColumnNamesFollowSpreadsheetLetters()
    {
        Assert.Equal("A", ProductionRecordExporter.ColumnName(0));
        Assert.Equal("Z", ProductionRecordExporter.ColumnName(25));
        Assert.Equal("AA", ProductionRecordExporter.ColumnName(26));
    }
}
