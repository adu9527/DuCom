namespace DuCom.Core.Diagnostics;

public enum LoadTargetBehavior
{
    Immediate,
    Slow,
    Failing,
}

public sealed record StandardLoadScenario(
    string Name,
    int Version,
    LoadGeneratorOptions GeneratorOptions,
    LoadTargetBehavior TargetBehavior,
    TimeSpan TargetDelay,
    int? FailAfterAcceptedBlocks,
    bool PaceByScheduledOffset);

public static class StandardLoadScenarios
{
    private const int DefaultSeed = 260826;

    public static IReadOnlyList<StandardLoadScenario> All { get; } =
    [
        Create("dual-1m-mixed", 100_000, TimeSpan.FromSeconds(10), LoadPayloadProfile.MixedNewline),
        Create("dual-1152000-sustained", 115_200, TimeSpan.FromMinutes(1), LoadPayloadProfile.MixedNewline),
        Create("dual-3m-burst", 300_000, TimeSpan.FromSeconds(10), LoadPayloadProfile.Burst),
        Create("no-newline-continuous", 100_000, TimeSpan.FromSeconds(10), LoadPayloadProfile.LongLine),
        Create("malformed-text-esc", 100_000, TimeSpan.FromSeconds(10), LoadPayloadProfile.MalformedBytes),
        Create(
            "slow-log-target",
            100_000,
            TimeSpan.FromSeconds(5),
            LoadPayloadProfile.MixedNewline,
            LoadTargetBehavior.Slow,
            TimeSpan.FromMilliseconds(2)),
        Create(
            "failing-log-target",
            100_000,
            TimeSpan.FromSeconds(5),
            LoadPayloadProfile.MixedNewline,
            LoadTargetBehavior.Failing,
            failAfterAcceptedBlocks: 32),
    ];

    public static StandardLoadScenario Get(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        StandardLoadScenario? scenario = All.FirstOrDefault(
            item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));

        return scenario ?? throw new ArgumentException(
            $"Unknown load scenario '{name}'. Available scenarios: {string.Join(", ", All.Select(item => item.Name))}.",
            nameof(name));
    }

    private static StandardLoadScenario Create(
        string name,
        long bytesPerSecond,
        TimeSpan duration,
        LoadPayloadProfile payloadProfile,
        LoadTargetBehavior targetBehavior = LoadTargetBehavior.Immediate,
        TimeSpan targetDelay = default,
        int? failAfterAcceptedBlocks = null) => new(
            name,
            1,
            new LoadGeneratorOptions(
                DefaultSeed,
                duration,
                bytesPerSecond,
                64,
                512,
                2,
                payloadProfile),
            targetBehavior,
            targetDelay,
            failAfterAcceptedBlocks,
            PaceByScheduledOffset: true);
}
