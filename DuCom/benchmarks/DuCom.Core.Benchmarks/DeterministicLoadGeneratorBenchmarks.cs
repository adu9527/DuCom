using BenchmarkDotNet.Attributes;
using DuCom.Core.Diagnostics;

namespace DuCom.Core.Benchmarks;

[MemoryDiagnoser]
public class DeterministicLoadGeneratorBenchmarks
{
    private readonly LoadGeneratorOptions _options = new(
        Seed: 260826,
        Duration: TimeSpan.FromMilliseconds(100),
        TargetBytesPerSecondPerPort: 100_000,
        MinimumChunkSize: 64,
        MaximumChunkSize: 512,
        PortCount: 2,
        PayloadProfile: LoadPayloadProfile.MixedNewline);

    [Benchmark]
    public int GenerateDualPortBlocks()
    {
        int payloadBytes = 0;
        foreach (GeneratedLoadBlock block in DeterministicLoadGenerator.Generate(_options))
        {
            payloadBytes += block.Payload.Length;
        }

        return payloadBytes;
    }
}
