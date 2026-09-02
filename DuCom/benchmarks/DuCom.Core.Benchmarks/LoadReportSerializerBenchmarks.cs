using BenchmarkDotNet.Attributes;
using DuCom.Core.Diagnostics;

namespace DuCom.Core.Benchmarks;

[MemoryDiagnoser]
public class LoadReportSerializerBenchmarks
{
    private readonly LoadReport _report = new(
        LoadReport.CurrentSchemaVersion,
        "benchmark-report",
        new DateTimeOffset(2026, 8, 26, 0, 0, 0, TimeSpan.Zero),
        "benchmark-worktree",
        new MachineInfo("benchmark-machine", "Windows", ".NET 10", 16),
        new LoadScenarioInfo("dual-1m-mixed", 1, 260826, TimeSpan.FromSeconds(10), 2, 100_000, "MixedNewline", "uniform-64-512"),
        new PipelineMetricsSnapshot(6963, 2_000_000, 6963, 2_000_000, 0, 0, 0, 0, 0, 0, 0, 0, ShutdownDrainState.Completed, TimeSpan.Zero),
        new ProcessMetricsSnapshot(TimeSpan.FromSeconds(10), 200_000, 300_000, 0, 0, 0, 20_000_000, 25_000_000, 24_000_000, 8_000_000, 10_000_000, 9_000_000, TimeSpan.FromMilliseconds(250), 12),
        "M0 benchmark fixture");

    [Benchmark]
    public string SerializeJson() => LoadReportSerializer.ToJson(_report);

    [Benchmark]
    public string SerializeMarkdown() => LoadReportSerializer.ToMarkdown(_report);
}
