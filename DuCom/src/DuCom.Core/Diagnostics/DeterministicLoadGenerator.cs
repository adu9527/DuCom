using System.Text;

namespace DuCom.Core.Diagnostics;

public enum LoadPayloadProfile
{
    Fixed,
    Random,
    Burst,
    LongLine,
    MixedNewline,
    Utf8,
    MalformedBytes,
    HexOriented,
}

public sealed record LoadGeneratorOptions(
    int Seed,
    TimeSpan Duration,
    long TargetBytesPerSecondPerPort,
    int MinimumChunkSize,
    int MaximumChunkSize,
    int PortCount,
    LoadPayloadProfile PayloadProfile)
{
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(Duration, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(TargetBytesPerSecondPerPort);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MinimumChunkSize);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaximumChunkSize, MinimumChunkSize);
        ArgumentOutOfRangeException.ThrowIfLessThan(PortCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(PortCount, 2);
    }
}

public sealed record GeneratedLoadBlock(
    int PortIndex,
    int Sequence,
    TimeSpan ScheduledOffset,
    byte[] Payload);

public static class DeterministicLoadGenerator
{
    private static readonly byte[] FixedPattern = "DuCom load block\r\n"u8.ToArray();
    private static readonly byte[] MixedNewlinePattern = "alpha\r\nbeta\ngamma\rdelta\r\n"u8.ToArray();
    private static readonly byte[] Utf8Pattern = Encoding.UTF8.GetBytes("DuCom UTF-8 中文日志 Ω\r\n");
    private static readonly byte[] MalformedPattern = [0xF0, 0x28, 0x8C, 0x28, 0x1B, 0x5B, 0x39, 0x39, 0x39, 0x6D, 0xFF, 0x0A];
    private static readonly byte[] HexPattern = "00 01 7F 80 FE FF AA 55\r\n"u8.ToArray();

    public static IEnumerable<GeneratedLoadBlock> Generate(LoadGeneratorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        List<GeneratedLoadBlock> blocks = [];
        long targetBytes = checked((long)Math.Round(
            options.TargetBytesPerSecondPerPort * options.Duration.TotalSeconds,
            MidpointRounding.AwayFromZero));

        for (int portIndex = 0; portIndex < options.PortCount; portIndex++)
        {
            DeterministicRandom random = new(unchecked((uint)options.Seed + (0x9E3779B9u * (uint)(portIndex + 1))));
            long generatedBytes = 0;
            int sequence = 0;

            while (generatedBytes < targetBytes)
            {
                int requestedSize = random.Next(options.MinimumChunkSize, options.MaximumChunkSize + 1);
                int chunkSize = (int)Math.Min(requestedSize, targetBytes - generatedBytes);
                byte[] payload = CreatePayload(options.PayloadProfile, random, portIndex, sequence, chunkSize);
                double scheduledBytes = options.PayloadProfile == LoadPayloadProfile.Burst
                    ? generatedBytes / (options.MaximumChunkSize * 4) * (options.MaximumChunkSize * 4)
                    : generatedBytes;
                TimeSpan offset = TimeSpan.FromSeconds(scheduledBytes / options.TargetBytesPerSecondPerPort);
                blocks.Add(new GeneratedLoadBlock(portIndex, sequence, offset, payload));
                generatedBytes += chunkSize;
                sequence++;
            }
        }

        return blocks
            .OrderBy(block => block.ScheduledOffset)
            .ThenBy(block => block.PortIndex)
            .ThenBy(block => block.Sequence);
    }

    private static byte[] CreatePayload(
        LoadPayloadProfile profile,
        DeterministicRandom random,
        int portIndex,
        int sequence,
        int length)
    {
        byte[] payload = new byte[length];

        if (profile == LoadPayloadProfile.Random || profile == LoadPayloadProfile.Burst)
        {
            random.NextBytes(payload);
            if (profile == LoadPayloadProfile.Burst && payload.Length > 0)
            {
                payload[0] = (byte)(sequence % 4 == 0 ? 0xFF : portIndex);
            }

            return payload;
        }

        ReadOnlySpan<byte> pattern = profile switch
        {
            LoadPayloadProfile.Fixed => FixedPattern,
            LoadPayloadProfile.LongLine => "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789"u8,
            LoadPayloadProfile.MixedNewline => MixedNewlinePattern,
            LoadPayloadProfile.Utf8 => Utf8Pattern,
            LoadPayloadProfile.MalformedBytes => MalformedPattern,
            LoadPayloadProfile.HexOriented => HexPattern,
            _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, null),
        };

        for (int index = 0; index < payload.Length; index++)
        {
            payload[index] = pattern[(index + sequence + portIndex) % pattern.Length];
        }

        return payload;
    }

    private struct DeterministicRandom(uint seed)
    {
        private uint _state = seed == 0 ? 0xA341316Cu : seed;

        public int Next(int minimumInclusive, int maximumExclusive)
        {
            uint range = checked((uint)(maximumExclusive - minimumInclusive));
            return minimumInclusive + (int)(NextUInt32() % range);
        }

        public void NextBytes(Span<byte> destination)
        {
            for (int index = 0; index < destination.Length; index++)
            {
                destination[index] = (byte)NextUInt32();
            }
        }

        private uint NextUInt32()
        {
            uint value = _state;
            value ^= value << 13;
            value ^= value >> 17;
            value ^= value << 5;
            _state = value;
            return value;
        }
    }
}
