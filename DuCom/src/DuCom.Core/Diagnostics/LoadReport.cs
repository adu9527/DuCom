namespace DuCom.Core.Diagnostics;

public sealed record MachineInfo(
    string MachineName,
    string OperatingSystem,
    string RuntimeVersion,
    int LogicalProcessorCount);

public sealed record LoadScenarioInfo(
    string ScenarioName,
    int Version,
    int Seed,
    TimeSpan Duration,
    int PortCount,
    long TargetBytesPerSecondPerPort,
    string PayloadProfile,
    string ChunkProfile);

public sealed record ProcessMetricsSnapshot(
    TimeSpan Elapsed,
    double ThroughputBytesPerSecond,
    double AllocationBytesPerSecond,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    long WorkingSetStartBytes,
    long WorkingSetPeakBytes,
    long WorkingSetEndBytes,
    long PrivateMemoryStartBytes,
    long PrivateMemoryPeakBytes,
    long PrivateMemoryEndBytes,
    TimeSpan CpuTime,
    int ThreadCount);

public sealed record LoadReport(
    int SchemaVersion,
    string ReportId,
    DateTimeOffset CreatedAtUtc,
    string WorktreeIdentifier,
    MachineInfo Machine,
    LoadScenarioInfo Scenario,
    PipelineMetricsSnapshot Pipeline,
    ProcessMetricsSnapshot Process,
    string? Notes,
    LoadCompletenessInfo? Completeness = null)
{
    public const int CurrentSchemaVersion = 3;
}
