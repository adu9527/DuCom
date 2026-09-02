using DuCom.Core.Diagnostics;

namespace DuCom.Core.Tests.Diagnostics;

public sealed class DeterministicLoadGeneratorTests
{
    [Theory]
    [InlineData(LoadPayloadProfile.Fixed)]
    [InlineData(LoadPayloadProfile.Random)]
    [InlineData(LoadPayloadProfile.Burst)]
    [InlineData(LoadPayloadProfile.LongLine)]
    [InlineData(LoadPayloadProfile.MixedNewline)]
    [InlineData(LoadPayloadProfile.Utf8)]
    [InlineData(LoadPayloadProfile.MalformedBytes)]
    [InlineData(LoadPayloadProfile.HexOriented)]
    public void SameOptionsProduceIdenticalDualPortSequence(LoadPayloadProfile profile)
    {
        LoadGeneratorOptions options = new(
            Seed: 12345,
            Duration: TimeSpan.FromMilliseconds(100),
            TargetBytesPerSecondPerPort: 20_000,
            MinimumChunkSize: 17,
            MaximumChunkSize: 83,
            PortCount: 2,
            PayloadProfile: profile);

        GeneratedLoadBlock[] first = DeterministicLoadGenerator.Generate(options).ToArray();
        GeneratedLoadBlock[] second = DeterministicLoadGenerator.Generate(options).ToArray();

        Assert.Equal(2, first.Select(block => block.PortIndex).Distinct().Count());
        Assert.Equal(first.Length, second.Length);
        Assert.Equal(
            first.Select(BlockIdentity),
            second.Select(BlockIdentity));
    }

    [Fact]
    public void GeneratorProducesExactTargetByteCountForEachPort()
    {
        LoadGeneratorOptions options = new(
            Seed: 7,
            Duration: TimeSpan.FromMilliseconds(250),
            TargetBytesPerSecondPerPort: 4_000,
            MinimumChunkSize: 64,
            MaximumChunkSize: 128,
            PortCount: 2,
            PayloadProfile: LoadPayloadProfile.MixedNewline);

        GeneratedLoadBlock[] blocks = DeterministicLoadGenerator.Generate(options).ToArray();

        Assert.All(
            blocks.GroupBy(block => block.PortIndex),
            port => Assert.Equal(1_000, port.Sum(block => block.Payload.Length)));
        Assert.All(
            blocks.GroupBy(block => block.PortIndex),
            port => Assert.Equal(Enumerable.Range(0, port.Count()), port.Select(block => block.Sequence)));
    }

    private static string BlockIdentity(GeneratedLoadBlock block) =>
        $"{block.PortIndex}:{block.Sequence}:{block.ScheduledOffset.Ticks}:{Convert.ToHexString(block.Payload)}";
}
